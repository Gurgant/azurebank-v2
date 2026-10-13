using System.Text.Json;
using AzureBank.Api.Converters;
using AzureBank.Shared.Enums;

namespace AzureBank.Tests.Unit.Converters;

/// <summary>
/// The converter the API reads every enum with, held on its own: it takes a name of the enum, in
/// any letter case, and refuses every other text, of which its refusal repeats a bounded part.
/// </summary>
/// <remarks>
/// <c>EnumAndHandleInputTests</c> holds the same rule through the host, on the one enum a request
/// carries. Here are the shapes no request has today: a member with a negative value, a nullable
/// enum, an enum as the key of a dictionary, an enum of flags, two names that differ only by case,
/// and the write side.
/// </remarks>
public class StrictJsonStringEnumConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new StrictJsonStringEnumConverterFactory() },
    };

    private enum Twin
    {
        Ab,
        aB,
    }

    [Flags]
    private enum Door
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    private sealed record Holder(AccountType? Type);

    private static T? Read<T>(string text) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(text), Options);

    [Theory]
    [InlineData("Savings", AccountType.Savings)]
    [InlineData("savings", AccountType.Savings)]
    [InlineData("SAVINGS", AccountType.Savings)]
    [InlineData("Checking", AccountType.Checking)]
    [InlineData("iNVESTMENT", AccountType.Investment)]
    public void Read_TakesANameOfTheEnum_InAnyLetterCase(string text, AccountType member)
    {
        Assert.Equal(member, Read<AccountType>(text));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("01")]
    [InlineData("-0")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Savings,Checking")]
    [InlineData("Savings, Checking")]
    [InlineData("Checking,Investment")]
    [InlineData(" Savings")]
    [InlineData("Savings ")]
    [InlineData(" ")]
    [InlineData("Bogus")]
    [InlineData("Deposit")]
    [InlineData("\u0661")]
    [InlineData("\uFF11")]
    [InlineData("\u017Favings")]
    [InlineData("Chec\u212Aing")]
    public void Read_RefusesATextThatIsNotAName_AndListsTheNames(string text)
    {
        // The last four are not ASCII: the digit one in Arabic-Indic and in full width, a long s
        // and a Kelvin sign. The comparison is ordinal, so none of them stands for a digit or
        // for a letter of a name.
        var refusal = Assert.Throws<JsonException>(() => Read<AccountType>(text));

        Assert.StartsWith(
            $"'{text}' is not a valid value for enum 'AccountType'. Valid values: Checking, Savings, Investment",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(64)]
    public void Read_RepeatsARefusedTextOfUpTo64Characters_Whole(int length)
    {
        var text = new string('x', length);

        var refusal = Assert.Throws<JsonException>(() => Read<AccountType>(text));

        Assert.StartsWith(
            $"'{text}' is not a valid value for enum 'AccountType'. Valid values: Checking, Savings, Investment",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(200)]
    [InlineData(5_000)]
    [InlineData(1_000_000)]
    public void Read_RepeatsOnlyTheFirst64CharactersOfALongerText_AndSaysHowManyThereWere(int length)
    {
        var text = new string('x', 63) + 'y' + new string('z', length - 64);

        var refusal = Assert.Throws<JsonException>(() => Read<AccountType>(text));

        Assert.StartsWith(
            $"'{new string('x', 63)}y' (shown in part: 64 of {length} characters) is not a valid value for enum "
            + "'AccountType'. Valid values: Checking, Savings, Investment",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("z", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ab\\r\\ncd", "abcd", 4, 6)]
    [InlineData("ab\\u0007\\u001B[31mcd", "ab[31mcd", 8, 10)]
    [InlineData("ab\\u2028cd\\u2029", "abcd", 4, 6)]
    [InlineData("ab\\u200Bcd\\u202E", "abcd", 4, 6)]
    [InlineData("a\\uD83D\\uDE00b", "ab", 2, 4)]
    [InlineData("\\r\\n", "", 0, 2)]
    public void Read_LeavesOutOfTheRepeatedTextWhatCanBreakALineOrDriveATerminal_AndSaysSo(
        string textAsJson, string shown, int shownLength, int length)
    {
        // A line break, a bell and an escape sequence, the two separators that some viewers
        // render as a line break, a zero-width space and a right-to-left override, a character
        // outside the basic plane, and a text that is nothing else.
        var refusal = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<AccountType>($"\"{textAsJson}\"", Options));

        Assert.StartsWith(
            $"'{shown}' (shown in part: {shownLength} of {length} characters) is not a valid value for enum "
            + "'AccountType'. Valid values: Checking, Savings, Investment",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CutsBeforeItLooksForWhatToLeaveOut_SoTheWorkDoesNotGrowWithTheText()
    {
        // 60 letters, six line breaks, then more letters: the six are inside the 64 that are
        // looked at, so 60 are shown, and the letters after them are not brought forward.
        var text = new string('x', 60) + new string('\n', 6) + new string('z', 100);

        var refusal = Assert.Throws<JsonException>(() => Read<AccountType>(text));

        Assert.StartsWith(
            $"'{new string('x', 60)}' (shown in part: 60 of 166 characters) is not a valid value for enum",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoNameOfAnEnumOfTheApi_IsLongerThanWhatARefusalRepeats()
    {
        // A name with one letter wrong is the refusal a person reads, and it is shown whole only
        // while every name fits in the part that is repeated.
        var names = new[] { typeof(AccountType).Assembly, typeof(StrictJsonStringEnumConverterFactory).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsEnum)
            .SelectMany(Enum.GetNames)
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(name.Length < 64, name));
    }

    [Fact]
    public void Read_RefusesTheEmptyText_WithItsOwnSentence()
    {
        var refusal = Assert.Throws<JsonException>(() => Read<AccountType>(string.Empty));

        Assert.StartsWith(
            "Empty string is not a valid value for enum 'AccountType'.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TakesTheNameOfAMemberWithANegativeValue_AndRefusesThatValueAsText()
    {
        Assert.Equal(TransactionStatus.Unspecified, Read<TransactionStatus>("Unspecified"));
        Assert.Throws<JsonException>(() => Read<TransactionStatus>("-1"));
    }

    [Fact]
    public void Read_OfANullableEnum_TakesNullAndAName_AndRefusesANumberAsText()
    {
        Assert.Null(JsonSerializer.Deserialize<Holder>("""{"type":null}""", Options)!.Type);
        Assert.Equal(
            AccountType.Savings, JsonSerializer.Deserialize<Holder>("""{"type":"savings"}""", Options)!.Type);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("""{"type":"1"}""", Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("""{"type":1}""", Options));
    }

    [Fact]
    public void AnEnumAsTheKeyOfADictionary_IsNotSupported_InEitherDirection()
    {
        // The converter does not say how to read or write a property name, so the serialiser
        // refuses the type itself, before any key is looked at.
        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Deserialize<Dictionary<AccountType, int>>("""{"Savings":1}""", Options));
        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(new Dictionary<AccountType, int> { [AccountType.Savings] = 1 }, Options));
    }

    [Fact]
    public void Read_OfAnEnumOfFlags_TakesOneName_AndRefusesAListOfThem()
    {
        Assert.Equal(Door.Read, Read<Door>("Read"));
        Assert.Throws<JsonException>(() => Read<Door>("Read, Write"));
        Assert.Throws<JsonException>(() => Read<Door>("3"));
    }

    [Fact]
    public void Read_WhenTwoNamesDifferOnlyByCase_TakesTheOneWrittenExactly()
    {
        Assert.Equal(Twin.Ab, Read<Twin>("Ab"));
        Assert.Equal(Twin.aB, Read<Twin>("aB"));
    }

    [Fact]
    public void NoEnumOfTheApi_HasTwoNamesThatDifferOnlyByCase()
    {
        // A text in a third casing would match two members, and which one it becomes is not
        // something a client should have to know.
        var enums = new[] { typeof(AccountType).Assembly, typeof(StrictJsonStringEnumConverterFactory).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsEnum)
            .ToList();

        Assert.NotEmpty(enums);
        Assert.All(enums, type =>
        {
            var names = Enum.GetNames(type);
            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void Write_WritesTheNameOfTheMember()
    {
        Assert.Equal("\"Savings\"", JsonSerializer.Serialize(AccountType.Savings, Options));
        Assert.Equal("\"Unspecified\"", JsonSerializer.Serialize(TransactionStatus.Unspecified, Options));
    }

    [Fact]
    public void Write_OfAValueThatIsNoMember_WritesItsDigits_WhichReadRefuses()
    {
        var written = JsonSerializer.Serialize((AccountType)99, Options);

        Assert.Equal("\"99\"", written);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AccountType>(written, Options));
    }
}
