namespace AlgoJudge.Server.Tests;

/// <summary>
/// The partition itself, checked without a database.
///
/// <para>
/// <b>This is the test that would have caught the nine.</b> It says nothing
/// about whether a copy is right; it says that somebody decided, for every
/// field, whether it travels. A field added to <c>Series</c> and to no list here
/// fails this test on the first run, which is a great deal earlier than a
/// contest discovering that its room restriction did not survive being copied.
/// </para>
/// </summary>
public class CopiedFieldsTests
{
    [Fact]
    public void Every_field_of_a_copy_is_classified()
    {
        foreach (var entity in CopiedFields.Entities)
        {
            var classified = new HashSet<string>(
                CopiedFields.Carried[entity]
                    .Concat(CopiedFields.Reset[entity])
                    .Concat(CopiedFields.Checked[entity]));

            var actual = CopiedFields.PropertiesOf(entity).Select(p => p.Name).ToHashSet();

            var unclassified = actual.Except(classified).OrderBy(n => n).ToList();
            Assert.True(unclassified.Count == 0,
                $"{entity.Name} has fields no copy has decided about: {string.Join(", ", unclassified)}. "
                + "Add each to Carried, Reset or Checked in CopiedFields — and to the copy itself "
                + "if it travels. Nine fields were dropped silently before this test existed.");

            var stale = classified.Except(actual).OrderBy(n => n).ToList();
            Assert.True(stale.Count == 0,
                $"{entity.Name} no longer has: {string.Join(", ", stale)}. A list naming a field "
                + "that was removed is a list nobody is reading.");
        }
    }

    /// <summary>
    /// A field cannot be in two buckets. Written because "carried and also
    /// reset" reads as a decision and is a contradiction.
    /// </summary>
    [Fact]
    public void No_field_is_classified_twice()
    {
        foreach (var entity in CopiedFields.Entities)
        {
            var all = CopiedFields.Carried[entity]
                .Concat(CopiedFields.Reset[entity])
                .Concat(CopiedFields.Checked[entity])
                .ToList();

            var twice = all.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0,
                $"{entity.Name} classifies twice: {string.Join(", ", twice)}");
        }
    }
}
