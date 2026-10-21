using CoreIns.SharedKernel.Paging;

namespace CoreIns.SharedKernel.Tests;

public sealed class KeysetPagingTests
{
    [Fact]
    public void Cursor_round_trips_its_parts()
    {
        var id = Guid.CreateVersion7();
        var cursor = KeysetPaging.Encode(KeysetPaging.Part(638000000000000000L), KeysetPaging.Part(id));

        KeysetPaging.TryDecode(cursor, 2, out var parts).ShouldBeTrue();
        parts.ShouldNotBeNull();
        KeysetPaging.TryLong(parts[0], out var ticks).ShouldBeTrue();
        ticks.ShouldBe(638000000000000000L);
        Guid.Parse(parts[1]).ShouldBe(id);
        cursor.ShouldNotContain("=");
        cursor.ShouldNotContain("+");
        cursor.ShouldNotContain("/");
    }

    [Theory]
    [InlineData("not a cursor")]
    [InlineData("25")]
    [InlineData("e30")]
    [InlineData("WyJhIl0")]
    public void A_malformed_cursor_or_one_with_the_wrong_part_count_is_refused(string cursor) =>
        KeysetPaging.TryDecode(cursor, 2, out _).ShouldBeFalse();

    [Fact]
    public void No_cursor_means_the_first_page()
    {
        KeysetPaging.TryDecode(null, 2, out var parts).ShouldBeTrue();
        parts.ShouldBeNull();
        KeysetPaging.TryDecode(string.Empty, 2, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, 25, true, 25)]
    [InlineData(1, 25, true, 1)]
    [InlineData(200, 25, true, 200)]
    [InlineData(0, 25, false, 0)]
    [InlineData(201, 25, false, 201)]
    [InlineData(-5, 25, false, -5)]
    public void The_strict_limit_is_one_to_the_cap(int? limit, int fallback, bool ok, int take)
    {
        KeysetPaging.TryTake(limit, fallback, out var result).ShouldBe(ok);
        result.ShouldBe(take);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(5000, 200)]
    public void The_lenient_limit_is_clamped(int? limit, int expected) => KeysetPaging.Clamp(limit).ShouldBe(expected);

    [Fact]
    public void Slice_returns_the_page_and_a_cursor_only_when_more_rows_remain()
    {
        var (page, next) = KeysetPaging.Slice([1, 2, 3], 2, i => KeysetPaging.Encode(i.ToString()));
        page.ShouldBe([1, 2]);
        KeysetPaging.TryDecode(next, 1, out var parts).ShouldBeTrue();
        parts![0].ShouldBe("2");

        var (last, none) = KeysetPaging.Slice([1, 2], 2, i => KeysetPaging.Encode(i.ToString()));
        last.ShouldBe([1, 2]);
        none.ShouldBeNull();
    }
}
