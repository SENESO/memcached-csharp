using MemcachedServer.Protocol;

namespace MemcachedServer.Tests;

public class CommandParserTests
{
    [Fact]
    public void EmptyLine_IsError()
    {
        var result = CommandParser.Parse("");
        Assert.Equal(TextProtocol.Error, result.ErrorReply);
    }

    [Fact]
    public void UnknownCommand_IsError()
    {
        var result = CommandParser.Parse("bogus something");
        Assert.Equal(TextProtocol.Error, result.ErrorReply);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Get_RequiresAtLeastOneKey()
    {
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse("get").ErrorReply);
    }

    [Fact]
    public void Get_MultipleKeys_Parses()
    {
        var result = CommandParser.Parse("get alpha beta gamma");
        Assert.Null(result.ErrorReply);
        Assert.Equal(CommandKind.Get, result.Command!.Kind);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, result.Command.Keys);
    }

    [Fact]
    public void Set_ParsesAllFields()
    {
        var result = CommandParser.Parse("set mykey 42 3600 5");
        Assert.Null(result.ErrorReply);
        var command = result.Command!;
        Assert.Equal(CommandKind.Set, command.Kind);
        Assert.Equal("mykey", command.Key);
        Assert.Equal(42u, command.Flags);
        Assert.Equal(3600L, command.Exptime);
        Assert.Equal(5, command.Bytes);
        Assert.False(command.NoReply);
    }

    [Fact]
    public void Set_WithNoReply_Parses()
    {
        var result = CommandParser.Parse("set mykey 0 0 5 noreply");
        Assert.Null(result.ErrorReply);
        Assert.True(result.Command!.NoReply);
    }

    [Theory]
    [InlineData("set k 0 0")]            // missing byte count
    [InlineData("set k x 0 3")]          // flags not numeric
    [InlineData("set k 0 0 -1")]         // negative byte count
    [InlineData("set k 0 0 3 sometimes")]// trailing token that is not noreply
    [InlineData("set")]                  // nothing at all
    public void Set_Malformed_IsBadCommandLine(string line)
    {
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse(line).ErrorReply);
    }

    [Fact]
    public void Cas_ParsesCasId()
    {
        var result = CommandParser.Parse("cas k 0 0 3 987654321");
        Assert.Null(result.ErrorReply);
        Assert.Equal(987654321ul, result.Command!.CasId);
    }

    [Fact]
    public void Cas_WithoutCasId_IsBadCommandLine()
    {
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse("cas k 0 0 3").ErrorReply);
    }

    [Fact]
    public void Incr_ParsesDelta_AndRequiresItNumeric()
    {
        var ok = CommandParser.Parse("incr counter 25");
        Assert.Null(ok.ErrorReply);
        Assert.Equal(25ul, ok.Command!.Delta);
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse("incr counter lots").ErrorReply);
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse("incr counter").ErrorReply);
    }

    [Fact]
    public void Delete_WithNoReply_Parses()
    {
        var result = CommandParser.Parse("delete k noreply");
        Assert.Null(result.ErrorReply);
        Assert.True(result.Command!.NoReply);
    }

    [Fact]
    public void FlushAll_DelayAndNoReply_InAnyOrder()
    {
        var a = CommandParser.Parse("flush_all 30 noreply");
        Assert.Null(a.ErrorReply);
        Assert.Equal(30, a.Command!.FlushDelay);
        Assert.True(a.Command.NoReply);

        var b = CommandParser.Parse("flush_all noreply");
        Assert.Null(b.ErrorReply);
        Assert.Equal(0, b.Command!.FlushDelay);
        Assert.True(b.Command.NoReply);

        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse("flush_all soon").ErrorReply);
    }

    [Fact]
    public void KeyLongerThan250_IsRejected()
    {
        var longKey = new string('k', CommandParser.MaxKeyLength + 1);
        Assert.Equal(TextProtocol.BadCommandLine, CommandParser.Parse($"get {longKey}").ErrorReply);
        Assert.Null(CommandParser.Parse($"get {new string('k', CommandParser.MaxKeyLength)}").ErrorReply);
    }

    [Fact]
    public void AbsurdDataBlockSize_ClosesConnection()
    {
        var result = CommandParser.Parse($"set k 0 0 {CommandParser.MaxDataBlockBytes + 1}");
        Assert.Equal(TextProtocol.BadCommandLine, result.ErrorReply);
        Assert.True(result.CloseConnection);
    }
}
