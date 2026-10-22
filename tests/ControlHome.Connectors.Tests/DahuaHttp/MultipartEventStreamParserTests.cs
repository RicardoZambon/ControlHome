using System.Text;
using ControlHome.Connectors.DahuaHttp.Protocol;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The attach stream's multipart framing, as firmware variants send it.</summary>
public sealed class MultipartEventStreamParserTests
{
    private static readonly string[] _cameraParts =
    [
        "Heartbeat",
        "Code=VideoMotion;action=Start;index=0",
        "Code=VideoMotion;action=Stop;index=0",
        "Code=CrossLineDetection;action=Start;index=0;data={\n   \"Class\" : \"Normal\",\n   \"Direction\" : \"LeftToRight\",\n   \"Name\" : \"Linha1\",\n   \"ObjectType\" : \"Human\"\n}",
        "Code=AlarmLocal;action=Start;index=0",
        "Code=NewFile;action=Pulse;index=0;data={\n   \"File\" : \"/mnt/sd/2026-10-21/001/dav/10/10.30.00-10.30.05[M][0@0][0].dav\",\n   \"Size\" : 1024\n}",
        "Heartbeat",
    ];

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Every_part_comes_out_whatever_the_line_ends_and_lengths(bool crlf, bool contentLength)
    {
        var parser = new MultipartEventStreamParser(DahuaFixtures.Boundary);

        var parts = Normalize([.. parser.Feed(DahuaFixtures.Multipart("event-stream-camera.txt", crlf, contentLength)), .. parser.Complete()]);

        parts.ShouldBe([.. _cameraParts, "Code=AlarmLocal;action=Stop;index=0"]);
    }

    [Fact]
    public void A_part_with_a_length_is_complete_without_waiting_for_the_next_delimiter()
    {
        // The next delimiter comes only with the next event or heartbeat (up to 5 s later): a counted part must not wait.
        var parser = new MultipartEventStreamParser("myboundary");

        parser.Feed(DahuaFixtures.Part("Code=VideoMotion;action=Start;index=0")).ShouldBe(["Code=VideoMotion;action=Start;index=0"]);
    }

    [Fact]
    public void Bytes_may_arrive_one_at_a_time()
    {
        var parser = new MultipartEventStreamParser(DahuaFixtures.Boundary);
        var parts = new List<string>();

        foreach (var b in DahuaFixtures.Multipart("event-stream-access.txt"))
        {
            parts.AddRange(parser.Feed([b]));
        }

        parts.AddRange(parser.Complete());
        parts.Count.ShouldBe(6);
        parts[0].ShouldBe("Heartbeat");
        parts[1].ShouldStartWith("Code=AccessControl;action=Pulse;index=0;data={");
        parts[1].ShouldEndWith("}");
        parts[5].ShouldBe("Code=AlarmLocal;action=Start;index=1");
    }

    [Fact]
    public void A_length_a_byte_short_still_yields_the_whole_line()
    {
        // Firmware examples count the body a byte or two short: the rest of the line still belongs to the part.
        var stream = "--myboundary\r\nContent-Type: text/plain\r\nContent-Length: 36\r\n\r\nCode=VideoMotion;action=Start;index=0\r\n\r\n";
        var parser = new MultipartEventStreamParser("myboundary");

        parser.Feed(Encoding.ASCII.GetBytes(stream)).ShouldBe(["Code=VideoMotion;action=Start;index=0"]);
    }

    [Fact]
    public void A_length_that_runs_into_the_next_delimiter_is_cut_there()
    {
        var stream = "--myboundary\r\nContent-Length: 200\r\n\r\nCode=VideoMotion;action=Start;index=0\r\n--myboundary\r\nContent-Length: 39\r\n\r\n"
            + "Code=VideoMotion;action=Stop;index=0\r\n\r\n";
        var parser = new MultipartEventStreamParser("myboundary");

        var parts = new List<string>(parser.Feed(Encoding.ASCII.GetBytes(stream)));
        parts.AddRange(parser.Complete());

        parts.ShouldBe(["Code=VideoMotion;action=Start;index=0", "Code=VideoMotion;action=Stop;index=0"]);
    }

    [Fact]
    public void Parts_without_headers_and_an_undeclared_boundary_are_understood()
    {
        var stream = "garbage before the first part\r\n--myboundary\r\nCode=AlarmLocal;action=Start;index=2\r\n--myboundary\r\nHeartbeat\r\n--myboundary--\r\n";
        var parser = new MultipartEventStreamParser(boundary: null);

        var parts = new List<string>(parser.Feed(Encoding.ASCII.GetBytes(stream)));
        parts.AddRange(parser.Complete());

        parts.ShouldBe(["Code=AlarmLocal;action=Start;index=2", "Heartbeat"]);
        parser.Delimiter.ShouldBe("--myboundary");
    }

    [Theory]
    [InlineData("myboundary")]
    [InlineData("\"myboundary\"")]
    [InlineData("--myboundary")]
    public void The_declared_boundary_may_be_quoted_or_already_prefixed(string boundary)
    {
        var parser = new MultipartEventStreamParser(boundary);

        parser.Feed(DahuaFixtures.Part("Code=VideoMotion;action=Start;index=0")).ShouldHaveSingleItem();
    }

    [Fact]
    public void A_boundary_declared_with_dashes_also_matches_rfc_delimiters()
    {
        // Declared "--myboundary": firmware sends "--myboundary" lines, but RFC 2046 makes the delimiter "----myboundary".
        var stream = "----myboundary\r\nContent-Type: text/plain\r\n\r\nCode=VideoMotion;action=Start;index=0\r\n----myboundary\r\nHeartbeat\r\n";
        var parser = new MultipartEventStreamParser("--myboundary");

        var parts = new List<string>(parser.Feed(Encoding.ASCII.GetBytes(stream)));
        parts.AddRange(parser.Complete());

        parts.ShouldBe(["Code=VideoMotion;action=Start;index=0", "Heartbeat"]);
    }

    [Fact]
    public void Multi_line_data_counted_short_still_comes_out_whole_without_waiting()
    {
        // Content-Length counted as if the lines ended in LF while the firmware sends CRLF: short by a byte per line, so
        // the count ends inside the data object. The part must keep the rest of the object (else the data is lost).
        var body = "Code=AccessControl;action=Pulse;index=0;data={\r\n   \"ErrorCode\" : 0,\r\n   \"Status\" : 1,\r\n   \"UserID\" : \"101\"\r\n}\r\n";
        var length = Encoding.UTF8.GetByteCount(body.Replace("\r\n", "\n", StringComparison.Ordinal));
        var stream = $"--myboundary\r\nContent-Type: text/plain\r\nContent-Length: {length}\r\n\r\n{body}\r\n";
        var parser = new MultipartEventStreamParser("myboundary");

        var part = parser.Feed(Encoding.ASCII.GetBytes(stream)).ShouldHaveSingleItem();

        var e = DahuaEventParser.Parse(part).ShouldHaveSingleItem();
        e.DataInteger("Status").ShouldBe(1);
        e.DataText("UserID").ShouldBe("101");
    }

    [Fact]
    public void Braces_inside_strings_do_not_hold_a_counted_part_back()
    {
        var body = "Code=CrossLineDetection;action=Pulse;index=0;data={\r\n   \"Name\" : \"Linha {1\"\r\n}\r\n\r\n";
        var stream = $"--myboundary\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        var parser = new MultipartEventStreamParser("myboundary");

        var part = parser.Feed(Encoding.ASCII.GetBytes(stream)).ShouldHaveSingleItem();

        DahuaEventParser.Parse(part).ShouldHaveSingleItem().DataText("Name").ShouldBe("Linha {1");
    }

    [Fact]
    public void Malformed_part_headers_are_tolerated()
    {
        var stream = "--myboundary\r\nContent-Length: lots\r\nX-Odd : : value\r\n\r\nCode=VideoMotion;action=Start;index=0\r\n--myboundary\r\nContent-Length: -5\r\n\r\nHeartbeat\r\n--myboundary\r\n";
        var parser = new MultipartEventStreamParser("myboundary");

        var parts = new List<string>(parser.Feed(Encoding.ASCII.GetBytes(stream)));
        parts.AddRange(parser.Complete());

        parts.ShouldBe(["Code=VideoMotion;action=Start;index=0", "Heartbeat"]);
    }

    [Fact]
    public void Every_split_of_the_stream_gives_the_same_parts()
    {
        var bytes = DahuaFixtures.Multipart("event-stream-access.txt");
        var whole = new MultipartEventStreamParser(DahuaFixtures.Boundary);
        var expected = new List<string>(whole.Feed(bytes));
        expected.AddRange(whole.Complete());

        for (var split = 1; split < bytes.Length; split += 7)
        {
            var parser = new MultipartEventStreamParser(DahuaFixtures.Boundary);
            var parts = new List<string>(parser.Feed(bytes.AsSpan(0, split)));
            parts.AddRange(parser.Feed(bytes.AsSpan(split)));
            parts.AddRange(parser.Complete());
            parts.ShouldBe(expected, $"split at {split}");
        }
    }

    [Fact]
    public void An_oversized_part_is_skipped_and_the_stream_recovers()
    {
        var parser = new MultipartEventStreamParser("myboundary", maxPartBytes: 128);
        var huge = "Code=VideoMotion;action=Start;index=0;data={\"x\":\"" + new string('a', 1000) + "\"}";

        var parts = new List<string>(parser.Feed(DahuaFixtures.Part(huge)));
        parts.AddRange(parser.Feed(DahuaFixtures.Part("Code=VideoMotion;action=Stop;index=0")));

        parts.ShouldBe(["Code=VideoMotion;action=Stop;index=0"]);
    }

    [Fact]
    public void An_endless_line_without_newline_is_dropped()
    {
        var parser = new MultipartEventStreamParser("myboundary", maxPartBytes: 128);

        parser.Feed(Encoding.ASCII.GetBytes(new string('x', 1000))).ShouldBeEmpty();
        parser.Feed(DahuaFixtures.Part("Heartbeat")).ShouldBe(["Heartbeat"]);
    }

    private static string[] Normalize(IEnumerable<string> parts) => [.. parts.Select(p => p.Replace("\r\n", "\n", StringComparison.Ordinal))];
}
