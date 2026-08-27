using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.DataProtection;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Where the keys that encrypt a session cookie live.
    /// <para>
    /// <b>Nothing configured them until 2026-08-27</b>, so the framework built
    /// its default key ring — local to the process and not durable. Every
    /// restart signed everybody out, a federated sign-in in flight lost its
    /// <c>state</c> and <c>nonce</c>, and a second instance could not read a
    /// cookie the first had minted. `docs/audits/SERVER_SCALING.md` §3.1 named
    /// it the highest-value change in that document.
    /// </para>
    /// <para>
    /// <b>The database, not Redis</b> (decided 2026-08-27, ADR
    /// <c>DATA_PROTECTION_KEY_RING_2026-08-27</c>). The reason is the operator
    /// rather than the engineering: a self-hosted installation should be one
    /// thing to back up and one thing to restore correctly at two in the
    /// morning. Redis is refused by name below rather than left out, so an
    /// installation that tries it is told, and adding it later is one arm of
    /// this switch.
    /// </para>
    /// <para>
    /// The shape — a <c>Kind</c>, a switch, and a refusal naming what it does
    /// not implement — is <c>Storage/BlobStoreRegistry</c>'s, deliberately: an
    /// operator who has configured storage here has already learnt it.
    /// </para>
    /// </summary>
    public static class KeyRing
    {
        public const string KindSetting = "DataProtection:Kind";
        public const string CertificatesSetting = "DataProtection:Certificates";

        /// <summary>The keys live in the database, beside everything else that has to survive.</summary>
        public const string Database = "database";

        /// <summary>
        /// The keys live in memory and die with the process — which is what an
        /// unconfigured Server did by accident.
        /// <para>
        /// It stays available because a development stack has no use for
        /// durable sessions, and because a state this product can be in should
        /// have a name. <b>It is refused outside Development</b>: on a real
        /// installation it presents as people being signed out at random, which
        /// is diagnosed as flakiness rather than as configuration.
        /// </para>
        /// </summary>
        public const string Ephemeral = "ephemeral";

        /// <summary>
        /// <b>Fixed, not configurable.</b> Data Protection derives its purpose
        /// strings from this, falling back to the content root, so two instances
        /// that do not agree on it will not share a ring even sharing a store —
        /// and changing it signs everybody out. A setting whose only two states
        /// are "correct" and "everybody is logged out" is not a setting.
        /// </summary>
        public const string ApplicationName = "AlgoJudge";

        /// <summary>
        /// Registers the ring and answers which kind is in force, for
        /// <see cref="Announce"/> to say out loud once the host is built.
        /// </summary>
        public static string Add(
            IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            var kind = configuration[KindSetting] is { Length: > 0 } named
                ? named.Trim().ToLowerInvariant()
                : Database;

            var certificates = Certificates(configuration);
            var protection = services.AddDataProtection().SetApplicationName(ApplicationName);

            switch (kind)
            {
                case Database:
                    protection.PersistKeysToDbContext<ApplicationDbContext>();
                    break;

                case Ephemeral when !environment.IsDevelopment():
                    throw new InvalidOperationException(
                        $"{KindSetting} is '{Ephemeral}', which keeps the keys in memory only: this "
                        + "installation would sign everybody out on every restart, and two instances "
                        + "of it would sign each other's visitors out. That is a development "
                        + $"arrangement, so it is refused here. Set {KindSetting}={Database}, or run "
                        + "with ASPNETCORE_ENVIRONMENT=Development if this really is a throwaway "
                        + "stack. See docs/specs/AUTHENTICATION.md §10.");

                case Ephemeral when certificates.Count > 0:
                    throw new InvalidOperationException(
                        $"{CertificatesSetting} is configured while {KindSetting} is '{Ephemeral}', "
                        + "which stores no keys for a certificate to encrypt. One of the two was "
                        + "meant, and guessing which would leave an installation believing its keys "
                        + "are protected when nothing is being stored at all.");

                case Ephemeral:
                    // Replaces the provider outright, so the application name
                    // above stops mattering for this kind. Left set regardless:
                    // it is a property of the installation, not of the store.
                    protection.UseEphemeralDataProtectionProvider();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"{KindSetting} is '{kind}', which this Server does not implement. It "
                        + $"understands '{Database}' — the keys in the database this installation "
                        + $"already has — and '{Ephemeral}', for a development stack. Redis is a "
                        + "second stateful service to back up and restore, and this product asks "
                        + "an installation for one; see docs/specs/AUTHENTICATION.md §10.");
            }

            if (certificates.Count > 0)
            {
                // **The first encrypts; every one listed decrypts.** Rotating a
                // certificate means putting the new one at the head and keeping
                // the old, because keys encrypted with a certificate nobody
                // supplies any more are keys nobody can read — which looks
                // exactly like having no key ring at all, the failure this class
                // exists to remove.
                protection.ProtectKeysWithCertificate(certificates[0]);
                protection.UnprotectKeysWithAnyCertificate([.. certificates]);
            }

            // What was decided, kept for the operator's endpoint to report.
            // Read from here rather than from configuration a second time: two
            // readers of one setting are two chances to disagree about what is
            // in force, and this one has already applied it.
            services.AddSingleton(new KeyRingSettings(kind, certificates));

            return kind;
        }

        /// <summary>
        /// One line, once the logger exists.
        /// <para>
        /// It is here because "everybody keeps getting signed out" is diagnosed
        /// by knowing which of these is in force, and the answer was previously
        /// nowhere: no endpoint reports it and no default announces itself.
        /// </para>
        /// </summary>
        public static void Announce(ILogger logger, string kind)
        {
            if (kind == Ephemeral)
            {
                logger.LogWarning(
                    "Data protection key ring: in memory ({Kind}). Sessions do not survive a "
                    + "restart and do not travel between instances.", kind);
                return;
            }

            logger.LogInformation(
                "Data protection key ring: {Kind}, application name {ApplicationName}.",
                kind, ApplicationName);
        }

        /// <summary>
        /// The certificates an operator supplied, in the order they were given.
        /// <para>
        /// Optional, and off by default: with nothing here the keys are stored
        /// as plain XML, and Data Protection says so at startup. Whoever can
        /// read that table can also write a row into <c>AspNetUsers</c>, so the
        /// certificate buys less than it looks — it is here for installations
        /// whose database is somebody else's to hold.
        /// </para>
        /// </summary>
        private static IReadOnlyList<X509Certificate2> Certificates(IConfiguration configuration)
        {
            var loaded = new List<X509Certificate2>();

            foreach (var declared in configuration.GetSection(CertificatesSetting).GetChildren())
            {
                var path = declared["Path"] is { Length: > 0 } stated
                    ? stated
                    : throw new InvalidOperationException(
                        $"{CertificatesSetting}:{declared.Key} has no Path");

                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"{CertificatesSetting}:{declared.Key} names '{path}', which is not there. "
                        + "Refused at startup rather than at the first sign-in: a key ring that "
                        + "cannot be read is an installation nobody can sign in to, and it should "
                        + "say so while somebody is still watching the deployment.");
                }

                try
                {
                    // **`EphemeralKeySet`** so loading a PFX never writes key
                    // material into a user profile the container does not keep.
                    loaded.Add(new X509Certificate2(
                        path, declared["Password"] ?? "", X509KeyStorageFlags.EphemeralKeySet));
                }
                catch (CryptographicException error)
                {
                    // The message names the password without repeating it: a
                    // wrong one and a corrupt file fail identically here, and
                    // the framework's own wording says neither.
                    throw new InvalidOperationException(
                        $"{CertificatesSetting}:{declared.Key} at '{path}' could not be read. It "
                        + "must be a PKCS#12 file carrying its private key, and Password must be "
                        + $"the one it was written with. ({error.Message})", error);
                }

                if (!loaded[^1].HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        $"{CertificatesSetting}:{declared.Key} at '{path}' carries no private key, "
                        + "so it could encrypt the key ring and never read it back.");
                }
            }

            return loaded;
        }
    }
}
