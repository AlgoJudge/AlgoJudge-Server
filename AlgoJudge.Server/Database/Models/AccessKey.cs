namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A secret this installation holds on behalf of a service it talks to,
    /// named so there can be more than one.
    /// <para>
    /// <b>A table rather than a column</b> because the first of these is the
    /// problem picker's dataset key and the next ones are expected to be
    /// something else entirely — a model provider's key, most likely. Naming
    /// them means a second one is a row rather than a migration.
    /// </para>
    /// <para>
    /// <b>These are readable, and that is the whole danger.</b> Every other
    /// secret in this product is write-only: a provider secret goes in and no
    /// endpoint answers with it. These are stored to be given back, and the only
    /// thing standing in front of one is the permission on the endpoint that
    /// gives it.
    /// </para>
    /// <para>
    /// <b>The one key there is no longer leaves this process</b> (2026-08-26).
    /// This said the value <i>had</i> to reach a manager's browser because the
    /// picker runs there; that stopped being true when UVa Explorer began minting
    /// hourly tokens, and the browser now gets one of those. What is stored here
    /// is what the token is minted <i>from</i> — see
    /// <c>Services/AccessKeyMinting.cs</c>.
    /// </para>
    /// <para>
    /// <b>So a new key is readable until somebody decides otherwise</b>, which is
    /// the sentence worth keeping: the next one to arrive should be asked whether
    /// it can be minted before it is handed out whole. And today there is one key
    /// and one gate — a second must bring its own, because an AI provider's
    /// credential handed out on the strength of "may import problems" is a
    /// mistake nobody would see until the bill arrived.
    /// </para>
    /// </summary>
    public class AccessKey
    {
        /// <summary>Lower-case, and the identifier — for example <c>uvaexplorer</c>.</summary>
        public required string Name { get; set; }

        /// <summary>The secret itself. Never logged, never projected except by the one endpoint that hands it out.</summary>
        public required string Value { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
