using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Files;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Files;

[Collection(AuthCollection.Name)]
public sealed class FileUploadTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string Password = "a long enough password";

    [Fact]
    public async Task Png_is_stored_as_webp_with_its_size()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var form = TestFiles.Form(TestImages.Png(640, 480), "plan.png");

        using var response = await admin.PostAsync("/api/files", form, Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.Created, Ct);
        var file = (await response.Content.ReadFromJsonAsync<StoredFileDto>(Ct))!;
        response.Headers.Location!.OriginalString.ShouldBe($"/api/files/{file.Id}");
        (file.ContentType, file.WidthPx, file.HeightPx).ShouldBe(("image/webp", 640, 480));
        file.Length.ShouldBeGreaterThan(0);

        var stored = await QueryDbAsync(db => db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct));
        (stored.Purpose, stored.ContentType, stored.Length).ShouldBe(("floor-background", "image/webp", file.Length));
        stored.Data.Length.ShouldBe((int)file.Length);
        stored.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored.Data)));
        ImageSignatures.Detect(stored.Data).ShouldBe(ImageFileFormat.Webp);

        var audit = (await AuditEntriesAsync("files.uploaded")).ShouldHaveSingleItem();
        audit.TargetId.ShouldBe(file.Id.ToString("D"));
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("purpose").GetString().ShouldBe("floor-background");
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public async Task Jpeg_and_webp_are_accepted(string format)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var data = format == "jpeg" ? TestImages.Jpeg(300, 200) : TestImages.Webp(300, 200);

        var file = await admin.UploadAsync(data, $"plan.{format}", Ct);

        (file.ContentType, file.WidthPx, file.HeightPx).ShouldBe(("image/webp", 300, 200));
    }

    [Fact]
    public async Task Large_image_is_scaled_down_to_4096_px()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        var file = await admin.UploadAsync(TestImages.Png(5000, 200), "wide.png", Ct);

        (file.WidthPx, file.HeightPx).ShouldBe((4096, 164));
    }

    [Fact]
    public async Task Metadata_is_dropped()
    {
        const string Marker = "controlhome-gps-marker-23.55S-46.63W";
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        var file = await admin.UploadAsync(TestImages.PngWithText(100, 100, Marker), "with-text.png", Ct);

        var bytes = await admin.GetByteArrayAsync($"/api/files/{file.Id}", Ct);
        Encoding.Latin1.GetString(bytes).ShouldNotContain(Marker);
    }

    [Fact]
    public async Task Get_returns_the_bytes_with_immutable_private_caching_and_nosniff()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var file = await admin.UploadAsync(TestImages.Png(64, 48), "plan.png", Ct);

        using var response = await admin.GetAsync($"/api/files/{file.Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/webp");
        response.Content.Headers.ContentLength.ShouldBe(file.Length);
        // NonValidated: the header as sent (the parsed CacheControlHeaderValue re-orders its directives).
        response.Headers.NonValidated["Cache-Control"].ToString().ShouldBe("private, max-age=31536000, immutable");
        response.Headers.NonValidated["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        using var image = TestImages.Decode(bytes);
        (image.Width, image.Height).ShouldBe((64, 48));
    }

    [Fact]
    public async Task Unknown_file_is_404_files_not_found()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.GetAsync($"/api/files/{Guid.NewGuid()}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("files.not_found");
    }

    [Fact]
    public async Task Svg_is_rejected_as_unsupported_type()
    {
        await AssertRejectedAsync(TestImages.Svg(), "plan.svg", "image/svg+xml", "files.unsupported_type");
    }

    [Fact]
    public async Task Html_renamed_to_png_is_rejected_as_unsupported_type()
    {
        await AssertRejectedAsync(TestImages.Html(), "plan.png", "image/png", "files.unsupported_type");
    }

    [Fact]
    public async Task Empty_file_is_rejected_as_unsupported_type()
    {
        await AssertRejectedAsync([], "plan.png", "image/png", "files.unsupported_type");
    }

    [Fact]
    public async Task File_over_10_mb_is_rejected_as_too_large()
    {
        // A PNG signature padded to 11 MB: the size check comes before anything else.
        var data = new byte[11 * 1024 * 1024];
        TestImages.Png(4, 4).CopyTo(data, 0);

        await AssertRejectedAsync(data, "huge.png", "image/png", "files.too_large");
    }

    [Fact]
    public async Task Image_declaring_too_many_pixels_is_rejected_as_too_large()
    {
        await AssertRejectedAsync(TestImages.PngDeclaring(20_000, 20_000), "bomb.png", "image/png", "files.too_large");
    }

    [Fact]
    public async Task Corrupt_image_is_rejected_as_invalid_image()
    {
        await AssertRejectedAsync(TestImages.CorruptPng(), "broken.png", "image/png", "files.invalid_image");
    }

    [Fact]
    public async Task Unknown_purpose_is_a_field_error()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var form = TestFiles.Form(TestImages.Png(10, 10), "avatar.png", purpose: "avatar");

        using var response = await admin.PostAsync("/api/files", form, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["purpose"]);
        (await QueryDbAsync(db => db.StoredFiles.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Upload_without_the_request_marker_header_is_refused()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var form = TestFiles.Form(TestImages.Png(10, 10), "plan.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        admin.DefaultRequestHeaders.Remove("X-ControlHome");

        using var response = await admin.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("request.missing_header");
    }

    [Fact]
    public async Task Member_cannot_upload()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        using var member = await Factory.LoginAsync("member", Password, Ct);
        using var form = TestFiles.Form(TestImages.Png(10, 10), "plan.png");

        using var response = await member.PostAsync("/api/files", form, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await QueryDbAsync(db => db.StoredFiles.CountAsync(Ct))).ShouldBe(0);
    }

    private async Task AssertRejectedAsync(byte[] data, string fileName, string contentType, string code)
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var form = TestFiles.Form(data, fileName, contentType: contentType);

        using var response = await admin.PostAsync("/api/files", form, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe(code);
        (await QueryDbAsync(db => db.StoredFiles.CountAsync(Ct))).ShouldBe(0);
        (await AuditEntriesAsync("files.uploaded")).ShouldBeEmpty();
    }
}
