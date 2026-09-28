using CaptionOverlay.Core.Captions;

namespace CaptionOverlay.Core.Tests.Captions;

public class CaptionBufferTests
{
    [Fact]
    public void Out_of_order_partials_are_ignored()
    {
        var buffer = new CaptionBuffer();
        var id = Guid.NewGuid();
        buffer.SetActiveUtterance(id);
        buffer.ApplyPartial(id, 2, "hello world").Should().BeTrue();
        buffer.ApplyPartial(id, 1, "hello").Should().BeFalse();
        buffer.Tentative!.Text.Should().Be("hello world");
    }

    [Fact]
    public void Partials_for_non_active_or_finalized_utterances_are_ignored()
    {
        var buffer = new CaptionBuffer();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        buffer.SetActiveUtterance(a);
        buffer.CommitFinal(a, "first", TimeSpan.Zero, TimeSpan.FromSeconds(1));
        buffer.ApplyPartial(a, 5, "late partial").Should().BeFalse();

        buffer.SetActiveUtterance(b);
        buffer.ApplyPartial(a, 6, "stale").Should().BeFalse();
        buffer.ApplyPartial(b, 7, "second").Should().BeTrue();
    }

    [Fact]
    public void Final_replaces_tentative_with_committed_line_and_raises_one_event()
    {
        var buffer = new CaptionBuffer();
        var id = Guid.NewGuid();
        buffer.SetActiveUtterance(id);
        buffer.ApplyPartial(id, 1, "hel");
        var events = new List<CaptionChangedEventArgs>();
        buffer.Changed += (_, e) => events.Add(e);

        buffer.CommitFinal(id, "Hello.", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        events.Should().ContainSingle().Which.Kind.Should().Be(CaptionChangeKind.Committed);
        buffer.Tentative.Should().BeNull();
        buffer.Committed.Should().ContainSingle().Which.Text.Should().Be("Hello.");
    }

    [Fact]
    public void Discard_clears_matching_tentative()
    {
        var buffer = new CaptionBuffer();
        var id = Guid.NewGuid();
        buffer.SetActiveUtterance(id);
        buffer.ApplyPartial(id, 1, "uh");
        buffer.Discard(id);
        buffer.Tentative.Should().BeNull();
    }

    [Fact]
    public void Snapshot_and_clear_display()
    {
        var buffer = new CaptionBuffer();
        for (int i = 0; i < 5; i++)
        {
            buffer.CommitFinal(Guid.NewGuid(), $"line {i}", TimeSpan.Zero, TimeSpan.Zero);
        }
        buffer.GetSnapshot(2).Lines.Select(l => l.Text).Should().Equal("line 3", "line 4");
        buffer.ClearDisplay();
        buffer.GetSnapshot(2).Lines.Should().BeEmpty();
        buffer.Committed.Should().HaveCount(5, "the transcript is kept");
        buffer.GetRecentText(13).Should().Be("line 3 line 4");
        buffer.GetRecentText(11).Should().Be("3 line 4", "prompts are cut at word boundaries");
    }

}

public class HallucinationFilterTests
{
    private readonly HallucinationFilter _filter = new();

    private FilterResult Apply(string text, float rms = 0.05f, string? previous = null, float? prob = null) =>
        _filter.Apply(new FilterInput(text, "de", previous, rms, prob));

    [Theory]
    [InlineData("")]
    [InlineData("  ...  ")]
    [InlineData("[Music]")]
    [InlineData("(Musik)")]
    [InlineData("♪ ♪")]
    [InlineData("*Applaus*")]
    public void Drops_empty_and_tag_only_results(string text) => Apply(text).Keep.Should().BeFalse();

    [Theory]
    [InlineData("Untertitel im Auftrag des ZDF, 2021")]
    [InlineData("Untertitelung des ZDF, 2020")]
    [InlineData("Vielen Dank fürs Zuschauen.")]
    [InlineData("Untertitel von Stephanie Geiges")]
    [InlineData("Thank you for watching.")]
    [InlineData("Thanks for watching!")]
    [InlineData("Subtitles by the Amara.org community")]
    public void Drops_known_silence_hallucinations(string text) => Apply(text).Keep.Should().BeFalse();

    [Theory]
    [InlineData("Vielen Dank für die Einladung.")]
    [InlineData("Thank you.")]
    [InlineData("Das ZDF berichtet heute über das Wetter.")]
    public void Keeps_normal_speech(string text) => Apply(text).Keep.Should().BeTrue();

    [Fact]
    public void Strips_tags_from_mixed_text()
    {
        var result = Apply("[Musik] Guten Abend, meine Damen und Herren.");
        result.Keep.Should().BeTrue();
        result.Text.Should().Be("Guten Abend, meine Damen und Herren.");
    }

    [Fact]
    public void Drops_repetition_only_on_low_energy_audio()
    {
        Apply("Ja, genau.", rms: 0.002f, previous: "Ja, genau.").Keep.Should().BeFalse();
        Apply("Ja, genau.", rms: 0.08f, previous: "Ja, genau.").Keep.Should().BeTrue();
    }

    [Fact]
    public void Drops_any_text_on_inaudible_audio()
    {
        Apply("Vielen Dank.", rms: 0f).Reason.Should().Be("no audible signal");
        Apply("Vielen Dank.", rms: 0.0005f).Keep.Should().BeFalse();
        Apply("Vielen Dank.", rms: 0.05f).Keep.Should().BeTrue();
    }

    [Fact]
    public void Drops_low_confidence_on_low_energy_audio()
    {
        Apply("irgendwas", rms: 0.003f, prob: 0.2f).Keep.Should().BeFalse();
        Apply("irgendwas", rms: 0.1f, prob: 0.2f).Keep.Should().BeTrue();
    }

    private static string Repeat(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));

    // Loops a small German model produced on real desktop audio (Speaches, 2026-09).
    public static TheoryData<string> LoopOnlyResults =>
    [
        Repeat("a lot ", 19).Trim(),
        "do you " + Repeat("do ", 60).Trim(),
        "The way " + Repeat("untuk ", 300).Trim(),
        Repeat("'u", 84),
    ];

    [Theory]
    [MemberData(nameof(LoopOnlyResults))]
    public void Drops_results_that_are_mostly_a_repetition_loop(string text) =>
        Apply(text).Reason.Should().Be("repetition loop");

    [Fact]
    public void Collapses_a_loop_at_the_end_of_real_speech_to_one_occurrence()
    {
        var result = Apply("Wir wollen jedes Jahr Millionen Gallonen an Treibstoff zu sparen zu sparen zu sparen zu sparen zu");
        result.Keep.Should().BeTrue();
        result.Text.Should().Be("Wir wollen jedes Jahr Millionen Gallonen an Treibstoff zu sparen");
    }

    [Fact]
    public void A_short_repetition_is_collapsed_not_dropped()
    {
        var result = Apply("Nein nein nein nein.");
        result.Keep.Should().BeTrue();
        result.Text.Should().Be("Nein");
    }

    [Fact]
    public void Loops_compare_words_without_case_and_punctuation()
    {
        HallucinationFilter.CollapseLoops("Er sagte: Nein, nein. Nein! Nein? Und ging.").Should().Be("Er sagte: Nein, Und ging.");
    }

    [Theory]
    [InlineData("Nein, nein, nein.")] // three times is still speech
    [InlineData("Das kostet 1000000000 Euro.")] // digits are not a character loop
    [InlineData("Hmmmm, hahaha, das ist lustig.")] // short character repeats stay
    [InlineData("Die Die Mauer fiel 1989.")]
    public void Leaves_ordinary_repetition_alone(string text) =>
        HallucinationFilter.CollapseLoops(text).Should().Be(text);

    [Fact]
    public void Partials_have_loops_collapsed_too() =>
        _filter.CleanPartial("Guten Abend " + Repeat("na ", 10)).Should().Be("Guten Abend na");
}
