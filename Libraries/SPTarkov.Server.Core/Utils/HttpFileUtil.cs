using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;

namespace SPTarkov.Server.Core.Utils;

[Injectable]
public class HttpFileUtil(HttpServerHelper httpServerHelper)
{
    public async Task SendFile(HttpResponse resp, string filePath)
    {
        var pathSlice = filePath.Split("/");
        var mimePath = httpServerHelper.GetMimeText(pathSlice[^1].Split(".")[^1]);
        var type = string.IsNullOrWhiteSpace(mimePath) ? httpServerHelper.GetMimeText("txt") : mimePath;
        var fileInfo = new FileInfo(filePath);
        resp.Headers.Append("Content-Type", type);
        resp.Headers.Append("Content-Length", fileInfo.Length.ToString());

        await resp.SendFileAsync(filePath, CancellationToken.None);
    }
}
