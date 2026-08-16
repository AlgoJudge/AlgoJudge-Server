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
    /// <b>These are readable, and that is the whole point and the whole
    /// danger.</b> Every other secret in this product is write-only: a provider
    /// secret goes in and no endpoint answers with it. This one has to reach a
    /// manager's browser, because the thing that needs it runs there. So it is
    /// stored to be given back, and the only thing standing in front of it is
    /// the permission on the endpoint that gives it.
    /// </para>
    /// <para>
    /// <b>Consequence, written down rather than discovered:</b> today there is
    /// one key and one gate. A second key must bring its own — an AI provider's
    /// credential handed out because somebody may import problems would be a
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
