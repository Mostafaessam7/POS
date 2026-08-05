using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

public sealed class EntityEqualityTests
{
    private sealed class Line : Entity<Guid>
    {
        public Line(Guid id) : base(id) { }
        public Line() { }
    }

    private sealed class OtherLine : Entity<Guid>
    {
        public OtherLine(Guid id) : base(id) { }
    }

    [Fact]
    public void Entities_of_the_same_type_with_the_same_id_are_equal()
    {
        var id = Guid.CreateVersion7();

        new Line(id).ShouldBe(new Line(id));
    }

    [Fact]
    public void Different_types_sharing_an_id_are_not_equal()
    {
        var id = Guid.CreateVersion7();

        new Line(id).Equals(new OtherLine(id)).ShouldBeFalse();
    }

    [Fact]
    public void Two_transient_entities_are_not_equal()
    {
        // The trap: without transient handling both hold default(Guid), compare
        // equal, and silently collapse to one element in a set.
        new Line().Equals(new Line()).ShouldBeFalse();
    }

    [Fact]
    public void Transient_entities_do_not_collapse_in_a_set()
    {
        var set = new HashSet<Line> { new(), new(), new() };

        set.Count.ShouldBe(3);
    }

    [Fact]
    public void A_transient_entity_equals_itself()
    {
        var line = new Line();

        line.Equals(line).ShouldBeTrue();
        set_contains(line).ShouldBeTrue();

        static bool set_contains(Line l) => new HashSet<Line> { l }.Contains(l);
    }

    [Fact]
    public void Equality_operators_agree_with_Equals()
    {
        var id = Guid.CreateVersion7();
        var a = new Line(id);
        var b = new Line(id);
        Line? nothing = null;

        (a == b).ShouldBeTrue();
        (a != b).ShouldBeFalse();
        (nothing == null).ShouldBeTrue();
        (a == nothing).ShouldBeFalse();
    }

    [Fact]
    public void Persisted_entities_with_the_same_id_share_a_hash_code()
    {
        var id = Guid.CreateVersion7();

        new Line(id).GetHashCode().ShouldBe(new Line(id).GetHashCode());
    }
}
