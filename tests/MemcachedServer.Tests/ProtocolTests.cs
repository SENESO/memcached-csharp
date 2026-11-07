using System.Text;
using MemcachedServer.Core;

namespace MemcachedServer.Tests;

/// <summary>
/// End-to-end protocol tests: raw bytes in, exact reply bytes out, over an
/// in-memory duplex transport running the real session and processor code.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public async Task SetGet_RoundTrip()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set foo 7 0 5\r\nhello\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        await h.Client.SendAsync("get foo\r\n");
        await h.Client.ExpectAsync("VALUE foo 7 5\r\nhello\r\nEND\r\n");
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsJustEnd()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("get ghost\r\n");
        await h.Client.ExpectAsync("END\r\n");
    }

    [Fact]
    public async Task Get_MultipleKeys_MixedHitsAndMisses()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 1\r\n1\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        await h.Client.SendAsync("get a missing\r\n");
        await h.Client.ExpectAsync("VALUE a 0 1\r\n1\r\nEND\r\n");
    }

    [Fact]
    public async Task Gets_AndCas_FullFlow()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set k 0 0 2\r\nv1\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        // Read the cas id the server reports via gets.
        await h.Client.SendAsync("gets k\r\n");
        var casId = h.Store.Get("k").CasId;
        await h.Client.ExpectAsync($"VALUE k 0 2 {casId}\r\nv1\r\nEND\r\n");

        await h.Client.SendAsync($"cas k 0 0 2 {casId + 5}\r\nv2\r\n");
        await h.Client.ExpectAsync("EXISTS\r\n");

        await h.Client.SendAsync($"cas k 0 0 2 {casId}\r\nv2\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        await h.Client.SendAsync("cas absent 0 0 1 123\r\nx\r\n");
        await h.Client.ExpectAsync("NOT_FOUND\r\n");
    }

    [Fact]
    public async Task Add_Replace_Delete_OverTheWire()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("add k 0 0 1\r\na\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("add k 0 0 1\r\nb\r\n");
        await h.Client.ExpectAsync("NOT_STORED\r\n");
        await h.Client.SendAsync("replace k 0 0 1\r\nc\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("replace absent 0 0 1\r\nc\r\n");
        await h.Client.ExpectAsync("NOT_STORED\r\n");

        await h.Client.SendAsync("delete k\r\n");
        await h.Client.ExpectAsync("DELETED\r\n");
        await h.Client.SendAsync("delete k\r\n");
        await h.Client.ExpectAsync("NOT_FOUND\r\n");
    }

    [Fact]
    public async Task Append_AndPrepend_OverTheWire()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set k 0 0 3\r\nmid\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("append k 0 0 3\r\nend\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("prepend k 0 0 5\r\nstart\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        await h.Client.SendAsync("get k\r\n");
        await h.Client.ExpectAsync("VALUE k 0 11\r\nstartmidend\r\nEND\r\n");
    }

    [Fact]
    public async Task IncrDecr_OverTheWire()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set n 0 0 2\r\n10\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        await h.Client.SendAsync("incr n 5\r\n");
        await h.Client.ExpectAsync("15\r\n");
        await h.Client.SendAsync("decr n 50\r\n");
        await h.Client.ExpectAsync("0\r\n");
        await h.Client.SendAsync("incr absent 1\r\n");
        await h.Client.ExpectAsync("NOT_FOUND\r\n");

        await h.Client.SendAsync("set s 0 0 3\r\nabc\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("incr s 1\r\n");
        await h.Client.ExpectAsync("CLIENT_ERROR cannot increment or decrement non-numeric value\r\n");
    }

    [Fact]
    public async Task Touch_OverTheWire()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set k 0 100 1\r\nv\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("touch k 200\r\n");
        await h.Client.ExpectAsync("TOUCHED\r\n");
        await h.Client.SendAsync("touch absent 200\r\n");
        await h.Client.ExpectAsync("NOT_FOUND\r\n");
    }

    [Fact]
    public async Task Expiry_OverTheWire_UsesServerClock()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set k 0 10 1\r\nv\r\n");
        await h.Client.ExpectAsync("STORED\r\n");

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        await h.Client.SendAsync("get k\r\n");
        await h.Client.ExpectAsync("END\r\n");
    }

    [Fact]
    public async Task NoReply_SilencesTheReply_ButStores()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set k 0 0 1 noreply\r\nx\r\n");
        await h.Client.ExpectNoReplyAsync();

        await h.Client.SendAsync("get k\r\n");
        await h.Client.ExpectAsync("VALUE k 0 1\r\nx\r\nEND\r\n");
    }

    [Fact]
    public async Task PipelinedCommands_InOneWrite_AllAnsweredInOrder()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 1\r\n1\r\nget a\r\nversion\r\n");
        await h.Client.ExpectAsync("STORED\r\nVALUE a 0 1\r\n1\r\nEND\r\nVERSION 1.0.0\r\n");
    }

    [Fact]
    public async Task CommandSplitAcrossWrites_IsReassembled()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 5\r\nhe");
        await h.Client.ExpectNoReplyAsync(200); // no reply until the block is complete
        await h.Client.SendAsync("llo\r\nget a\r\n");
        await h.Client.ExpectAsync("STORED\r\nVALUE a 0 5\r\nhello\r\nEND\r\n");
    }

    [Fact]
    public async Task UnknownCommand_AndBadFormat_Errors()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("frobnicate\r\n");
        await h.Client.ExpectAsync("ERROR\r\n");
        await h.Client.SendAsync("set onlykey\r\n");
        await h.Client.ExpectAsync("CLIENT_ERROR bad command line format\r\n");
        // The connection stays usable after both errors.
        await h.Client.SendAsync("version\r\n");
        await h.Client.ExpectAsync("VERSION 1.0.0\r\n");
    }

    [Fact]
    public async Task OversizeItem_ServerError_AndConnectionSurvives()
    {
        await using var h = SessionHarness.Start(maxValueBytes: 4);
        await h.Client.SendAsync("set big 0 0 5\r\n12345\r\n");
        await h.Client.ExpectAsync("SERVER_ERROR object too large for cache\r\n");

        await h.Client.SendAsync("set small 0 0 3\r\nabc\r\nget small\r\n");
        await h.Client.ExpectAsync("STORED\r\nVALUE small 0 3\r\nabc\r\nEND\r\n");
    }

    [Fact]
    public async Task BadDataChunk_ReportsError_AndCloses()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 3\r\nabcXX");
        await h.Client.ExpectAsync("CLIENT_ERROR bad data chunk\r\n");
        Assert.True(await h.SessionEndedAsync());
    }

    [Fact]
    public async Task Stats_ReportsTheCacheState()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 1\r\n1\r\nget a\r\nget missing\r\n");
        await h.Client.ExpectAsync("STORED\r\nVALUE a 0 1\r\n1\r\nEND\r\nEND\r\n");

        await h.Client.SendAsync("stats\r\n");
        await h.Client.ExpectContainsAsync("STAT version 1.0.0\r\n");
        await h.Client.ExpectContainsAsync("STAT curr_items 1\r\n");
        await h.Client.ExpectContainsAsync("STAT cmd_get 2\r\n");
        await h.Client.ExpectContainsAsync("STAT get_hits 1\r\n");
        await h.Client.ExpectContainsAsync("STAT get_misses 1\r\n");
        await h.Client.ExpectContainsAsync("END\r\n");
    }

    [Fact]
    public async Task FlushAll_OverTheWire()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("set a 0 0 1\r\n1\r\n");
        await h.Client.ExpectAsync("STORED\r\n");
        await h.Client.SendAsync("flush_all\r\n");
        await h.Client.ExpectAsync("OK\r\n");
        await h.Client.SendAsync("get a\r\n");
        await h.Client.ExpectAsync("END\r\n");
    }

    [Fact]
    public async Task Quit_ClosesTheSession_WithoutAReply()
    {
        await using var h = SessionHarness.Start();
        await h.Client.SendAsync("quit\r\n");
        await h.Client.ExpectNoReplyAsync(200);
        Assert.True(await h.SessionEndedAsync());
    }

    [Fact]
    public async Task BinaryValues_RoundTripUnchanged()
    {
        await using var h = SessionHarness.Start();
        // A payload containing a NUL, a non-ASCII byte and a space must survive intact.
        var payload = new byte[] { (byte)'a', 0, (byte)'b', 0xFF, (byte)' ' };
        h.Store.Store(StorageKind.Set, "bin", 3, 0, payload);

        await h.Client.SendAsync("get bin\r\n");
        var expected = new List<byte>();
        expected.AddRange(Encoding.ASCII.GetBytes("VALUE bin 3 5\r\n"));
        expected.AddRange(payload);
        expected.AddRange(Encoding.ASCII.GetBytes("\r\nEND\r\n"));
        await h.Client.ExpectBytesAsync(expected.ToArray());
    }
}
