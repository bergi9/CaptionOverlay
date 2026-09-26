using CaptionOverlay.Core.Captions;

namespace CaptionOverlay.Core.Tests.Captions;

public class CaptionLineSplitterTests
{
    // Monospace: one character = one unit.
    private static IReadOnlyList<string> Split(string text, double width) => CaptionLineSplitter.Split(text, width, s => s.Length);

    [Fact]
    public void Text_that_fits_stays_one_row() =>
        Split("Short line.", 40).Should().Equal("Short line.");

    [Fact]
    public void Rows_fill_up_to_the_width_and_break_between_words()
    {
        var rows = Split("one two three four five six seven eight nine ten", 20);
        rows.Should().Equal("one two three four", "five six seven eight", "nine ten");
        rows.Should().AllSatisfy(r => r.Length.Should().BeLessThanOrEqualTo(20));
    }

    [Fact]
    public void A_sentence_end_is_preferred_when_the_row_is_at_least_half_full()
    {
        // "The results said otherwise." is 27 of 40: break there instead of pulling "The median" up.
        Split("The results said otherwise. The median PR throughput rose.", 40)
            .Should().Equal("The results said otherwise.", "The median PR throughput rose.");
    }

    [Fact]
    public void A_sentence_end_early_in_the_row_is_not_used()
    {
        // "Yes." would leave the row 10 % full: fill it instead.
        Split("Yes. And then we went home to see what the others did there.", 40)
            .Should().Equal("Yes. And then we went home to see what", "the others did there.");
    }

    [Fact]
    public void A_comma_is_used_when_there_is_no_sentence_end()
    {
        Split("Depending on the language and the method, that number varies a lot.", 45)
            .Should().Equal("Depending on the language and the method,", "that number varies a lot.");
    }

    [Fact]
    public void Closing_quotes_after_a_sentence_end_count()
    {
        Split("He said \"we are done here.\" Then everyone left the room.", 30)
            .Should().Equal("He said \"we are done here.\"", "Then everyone left the room.");
    }

    [Fact]
    public void A_word_longer_than_the_row_gets_a_row_of_its_own() =>
        Split("a Donaudampfschifffahrtsgesellschaft b", 10).Should().Equal("a", "Donaudampfschifffahrtsgesellschaft", "b");

    [Fact]
    public void No_width_means_no_splitting_and_whitespace_is_normalized()
    {
        Split("  one   two  ", 0).Should().Equal("one two");
        Split("   ", 10).Should().BeEmpty();
    }
}
