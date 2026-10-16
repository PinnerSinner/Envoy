using Envoy.Core.Chat;
using Xunit;

namespace Envoy.Core.Tests;

public class ChatTextCleanerTests
{
    [Theory]
    [InlineData("Bob:​ hi", "Bob: hi")]
    [InlineData("﻿Bob: hi", "Bob: hi")]
    [InlineData("Bob: h�i", "Bob: hi")]
    [InlineData("Bob: hi", "Bob: hi")]
    [InlineData("Bob: hi️", "Bob: hi")]
    [InlineData("Bob: hi\u0007", "Bob: hi")]
    [InlineData("Bob:\t  hi    there ", "Bob: hi there")]
    [InlineData("Bob: \uD800hi", "Bob: hi")]
    public void Removes_invisible_and_replacement_chars(string input, string expected) =>
        Assert.Equal(expected, ChatTextCleaner.Clean(input));

    [Theory]
    [InlineData("■ [All] Bob: hi", "[All] Bob: hi")]
    [InlineData("© ■ Bob: hi", "Bob: hi")]
    [InlineData("[All] © Bob: hi", "[All] Bob: hi")]
    [InlineData("12:04 ▶ Bob: hi", "12:04 Bob: hi")]
    [InlineData("| Kim: gg", "Kim: gg")]
    [InlineData("[CLAN] Bob: hi", "[CLAN] Bob: hi")]
    [InlineData("(Team) Bob: hi", "(Team) Bob: hi")]
    [InlineData("■Bob: hi", "■Bob: hi")]
    [InlineData("Bob: ■ hi", "Bob: ■ hi")]
    [InlineData("■", "■")]
    public void Strips_leading_icon_junk_not_brackets(string input, string expected) =>
        Assert.Equal(expected, ChatTextCleaner.Clean(input));

    [Theory]
    [InlineData("Kim: ㅋㅋㅋ")]
    [InlineData("Kim: ㄱㄱ")]
    [InlineData("Kim: ㅈㅈ")]
    public void Keeps_hangul_compat_jamo(string line) => Assert.Equal(line, ChatTextCleaner.Clean(line));

    [Fact]
    public void Folds_fullwidth_ascii()
    {
        Assert.Equal("Player1: gg wp", ChatTextCleaner.Clean("Ｐｌａｙｅｒ１: ｇｇ ｗｐ"));
        Assert.Equal("小明：你好", ChatTextCleaner.Clean("小明：你好")); // full-width colon and CJK stay
    }

    [Theory]
    [InlineData("»Bob«", "Bob")]
    [InlineData("[CLAN] Bob", "[CLAN] Bob")]
    [InlineData("Bob ■", "Bob")]
    [InlineData("(Bob)", "(Bob)")]
    [InlineData("_Bob_", "_Bob_")]
    [InlineData("Bob)", "Bob")]
    [InlineData("■", "■")]
    public void Clean_sender_keeps_clan_brackets(string input, string expected) =>
        Assert.Equal(expected, ChatTextCleaner.CleanSender(input));

    [Fact]
    public void Symbols_off_still_removes_invisibles()
    {
        Assert.Equal("■ Bob: hi", ChatTextCleaner.Clean("■ Bob:​ hi�", symbols: false));
        Assert.Equal("Ｐ: hi", ChatTextCleaner.Clean("Ｐ: hi", symbols: false));
    }
}
