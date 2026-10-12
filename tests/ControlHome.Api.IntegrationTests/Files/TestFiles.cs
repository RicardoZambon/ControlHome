using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Files;

namespace ControlHome.Api.IntegrationTests.Files;

/// <summary>Multipart upload helpers (<c>POST /api/files</c>: <c>file</c> + <c>purpose</c>).</summary>
internal static class TestFiles
{
    public static MultipartFormDataContent Form(byte[] data, string fileName, string purpose = "floor-background", string contentType = "image/png")
    {
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent
        {
            { file, "file", fileName },
            { new StringContent(purpose), "purpose" },
        };
    }

    /// <summary>Uploads a floor background that must be accepted.</summary>
    public static async Task<StoredFileDto> UploadAsync(this HttpClient client, byte[] data, string fileName, CancellationToken cancellationToken)
    {
        using var form = Form(data, fileName);
        using var response = await client.PostAsync("/api/files", form, cancellationToken);
        await response.ShouldHaveStatusAsync(HttpStatusCode.Created, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>(cancellationToken))!;
    }
}
