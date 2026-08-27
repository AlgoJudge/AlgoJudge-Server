using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>What <see cref="KeyRing"/> decided, for whoever has to report it.</summary>
    public sealed record KeyRingSettings(string Kind, IReadOnlyList<X509Certificate2> Certificates);

    public sealed record KeyRingKeyState(
        string Id,
        DateTimeOffset CreatedAt,
        DateTimeOffset ActivatesAt,
        DateTimeOffset ExpiresAt,
        bool IsActive,
        bool IsRevoked,
        string Storage,
        bool Readable);

    public sealed record KeyRingCertificateState(
        string Subject, string Thumbprint, DateTimeOffset NotAfter, bool Encrypts);

    public sealed record KeyRingState(
        string Kind,
        string ApplicationName,
        IReadOnlyList<KeyRingCertificateState> Certificates,
        IReadOnlyList<KeyRingKeyState> Keys,
        IReadOnlyList<string> Problems);

    public interface IKeyRingOperations
    {
        KeyRingState Report();

        /// <summary>A new key, active now. Nobody is signed out.</summary>
        KeyRingKeyState Rotate();

        /// <summary>Every key revoked. Everybody is signed out.</summary>
        int RevokeAll(string reason);
    }

    /// <summary>
    /// The operator's view of the key ring, and the two things they can do to it.
    /// <para>
    /// <b>This exists because the documented recipe was raw SQL.</b> Getting an
    /// encrypted key on an installation that already had a plaintext one meant
    /// `DELETE FROM "DataProtectionKeys"` against the database — the kind of
    /// instruction <c>aj-admin</c> exists to remove, and one that deletes the
    /// evidence rather than revoking it.
    /// </para>
    /// <para>
    /// <b>Rotate and revoke are not the same act and the difference is the whole
    /// point.</b> Rotating adds a key and signs nobody out, so an installation
    /// that has just configured a certificate gets an encrypted <i>current</i>
    /// key while every existing session keeps working — and the old plaintext key
    /// stays readable, which is what makes that possible. Revoking makes every
    /// existing key unusable: everybody is signed out, and a key that leaked in a
    /// database dump can no longer mint a cookie this Server will accept.
    /// </para>
    /// <para>
    /// <b>Neither reaches another instance quickly.</b> Data Protection refreshes
    /// its key ring on a timer, and a write by one process does not notify
    /// another — so with several instances a revoke takes effect here at once and
    /// elsewhere within the refresh period. Said out loud because "signed out"
    /// that is only true of one instance is worse than either answer.
    /// </para>
    /// </summary>
    public sealed class KeyRingOperations(
        KeyRingSettings settings,
        IServiceProvider services,
        IOptions<KeyManagementOptions> options
    ) : IKeyRingOperations
    {
        /// <summary>
        /// Resolved lazily, and not injected.
        /// <para>
        /// On <c>ephemeral</c> the provider is replaced outright and no key
        /// manager governs what is actually in use, so asking for one in the
        /// constructor would make this class unconstructable on a development
        /// stack — where an operator is most likely to run it and be told why.
        /// </para>
        /// </summary>
        private IKeyManager Keys => (IKeyManager)services.GetRequiredService(typeof(IKeyManager));

        private bool Ephemeral => settings.Kind == KeyRing.Ephemeral;

        public KeyRingState Report()
        {
            var certificates = settings.Certificates
                .Select((certificate, index) => new KeyRingCertificateState(
                    certificate.Subject,
                    // The first eight characters: enough to tell two
                    // certificates apart in a report, and not a copy of the
                    // identifier somebody might paste somewhere it matters.
                    certificate.Thumbprint[..8],
                    certificate.NotAfter,
                    Encrypts: index == 0))
                .ToList();

            if (Ephemeral)
            {
                return new KeyRingState(
                    settings.Kind, KeyRing.ApplicationName, certificates, [],
                    ["The keys are in memory. They are lost on the next restart and do not "
                     + "travel between instances, so there is nothing here to rotate or revoke."]);
            }

            var stored = Stored();
            var now = DateTimeOffset.UtcNow;
            var keys = new List<KeyRingKeyState>();

            foreach (var key in Keys.GetAllKeys().OrderBy(k => k.CreationDate))
            {
                // **Reading the descriptor is the test.** It deserialises, which
                // decrypts — so a key encrypted with a certificate nobody
                // supplies any more fails exactly here, which is the failure
                // this report exists to find before an operator meets it as
                // everybody being signed out.
                var readable = true;
                try { _ = key.Descriptor; }
                catch (Exception) { readable = false; }

                keys.Add(new KeyRingKeyState(
                    key.KeyId.ToString(),
                    key.CreationDate,
                    key.ActivationDate,
                    key.ExpirationDate,
                    IsActive: !key.IsRevoked && key.ActivationDate <= now && key.ExpirationDate > now,
                    key.IsRevoked,
                    stored.TryGetValue(key.KeyId, out var how) ? how : "unknown",
                    readable));
            }

            return new KeyRingState(
                settings.Kind, KeyRing.ApplicationName, certificates, keys,
                Problems(keys, certificates, now));
        }

        private static List<string> Problems(
            IReadOnlyList<KeyRingKeyState> keys,
            IReadOnlyList<KeyRingCertificateState> certificates,
            DateTimeOffset now)
        {
            var problems = new List<string>();

            if (keys.Count(k => !k.Readable) is > 0 and var unreadable)
            {
                problems.Add(
                    $"{unreadable} key(s) cannot be read. A certificate that encrypted them is "
                    + "not in DataProtection:Certificates any more; put it back, at the end of "
                    + "the list, or every session minted under them is already lost.");
            }

            if (!keys.Any(k => k.IsActive))
            {
                problems.Add(
                    "No key is active. The next request mints one, which is ordinary on a fresh "
                    + "installation and worth a second look on an old one.");
            }

            // **Two problems, not one, and the difference is what a rotate can
            // and cannot fix.** The first test written for this asserted that
            // rotating cleared "the keys are plaintext", and it did not — because
            // the old plaintext key is still there and still usable, which is
            // exactly what makes rotating non-disruptive. Reporting one problem
            // would have had to be either un-clearable or a lie.
            var usable = keys.Where(k => k.IsActive).ToList();

            // The key new cookies are minted under. Ordered the way Data
            // Protection's own resolver prefers — most recently activated —
            // rather than reimplementing it: this is a report, and being one
            // key out would only ever over-warn.
            if (certificates.Count > 0
                && usable.OrderByDescending(k => k.ActivatesAt).FirstOrDefault()
                    is { Storage: "plaintext" })
            {
                problems.Add(
                    "A certificate is configured and the key new cookies are minted under is "
                    + "stored in plain text. Data Protection encrypts a key when it writes one, "
                    + "so this stays true until the ring rotates on its own. "
                    + "`aj-admin keyring rotate` writes an encrypted key now and signs nobody out.");
            }

            // **And the one a rotate deliberately leaves standing.** A dump
            // taken while this is true still carries something that can mint a
            // cookie this Server accepts.
            if (certificates.Count > 0 && usable.Any(k => k.Storage == "plaintext"))
            {
                var last = usable.Where(k => k.Storage == "plaintext").Max(k => k.ExpiresAt);
                problems.Add(
                    $"{usable.Count(k => k.Storage == "plaintext")} usable key(s) are still "
                    + "stored in plain text, so a database dump still carries what can mint a "
                    + $"session cookie. They stop being usable on {last:yyyy-MM-dd}. "
                    + "`aj-admin keyring revoke --yes` ends it now, and signs everybody out.");
            }

            foreach (var expired in certificates.Where(c => c.NotAfter < now))
            {
                problems.Add(
                    $"The certificate {expired.Subject} ({expired.Thumbprint}) expired on "
                    + $"{expired.NotAfter:yyyy-MM-dd}. Keys it already encrypted are still read "
                    + "with it; nothing will renew it on its own.");
            }

            return problems;
        }

        public KeyRingKeyState Rotate()
        {
            Refuse();

            var now = DateTimeOffset.UtcNow;
            var key = Keys.CreateNewKey(now, now.Add(options.Value.NewKeyLifetime));

            return new KeyRingKeyState(
                key.KeyId.ToString(), key.CreationDate, key.ActivationDate, key.ExpirationDate,
                IsActive: true, IsRevoked: false,
                Stored().TryGetValue(key.KeyId, out var how) ? how : "unknown",
                Readable: true);
        }

        public int RevokeAll(string reason)
        {
            Refuse();

            var revoked = Keys.GetAllKeys().Count(key => !key.IsRevoked);
            Keys.RevokeAllKeys(DateTimeOffset.UtcNow, reason);
            return revoked;
        }

        /// <summary>
        /// <c>ephemeral</c> has a key manager and it governs nothing: the
        /// provider was replaced, so rotating would write into a store the
        /// running Server never reads and report success for an act that did
        /// nothing.
        /// </summary>
        private void Refuse()
        {
            if (Ephemeral)
            {
                throw new Utils.ValidationException(
                    "This Server is running with the keys in memory, so there is no stored ring "
                    + $"to change. Set {KeyRing.KindSetting}={KeyRing.Database} first.",
                    "keyring.ephemeral");
            }
        }

        /// <summary>
        /// How each key is written, read from the ring's own XML rather than
        /// from the table.
        /// <para>
        /// <c>IXmlRepository</c> is the store Data Protection actually uses, so
        /// this answers for whichever one is configured instead of assuming the
        /// keys are rows. The shapes are the framework's:
        /// <c>encryptedSecret</c> where an encryptor was configured when the key
        /// was written, and a <c>masterKey</c> element where none was.
        /// </para>
        /// </summary>
        private Dictionary<Guid, string> Stored()
        {
            var how = new Dictionary<Guid, string>();
            if (options.Value.XmlRepository is not { } repository) return how;

            foreach (var element in repository.GetAllElements())
            {
                if (element.Name.LocalName != "key") continue;
                if (!Guid.TryParse(element.Attribute("id")?.Value, out var id)) continue;

                how[id] = element.Descendants().Any(e => e.Name.LocalName == "encryptedSecret")
                    ? "encrypted"
                    : element.Descendants().Any(e => e.Name.LocalName == "masterKey")
                        ? "plaintext"
                        : "unknown";
            }

            return how;
        }
    }
}
