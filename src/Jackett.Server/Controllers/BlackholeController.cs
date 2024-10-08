using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json.Linq;
using NLog;

namespace Jackett.Server.Controllers
{
    [AllowAnonymous]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("bh/{indexerId}")]
    public class BlackholeController : Controller
    {
        private readonly Logger _logger;
        private readonly IIndexerManagerService _indexerService;
        private readonly ServerConfig _serverConfig;
        private readonly IProtectionService _protectionService;

        public BlackholeController(IIndexerManagerService i, Logger l, ServerConfig sConfig, IProtectionService ps)
        {
            _logger = l;
            _indexerService = i;
            _serverConfig = sConfig;
            _protectionService = ps;
        }

        [HttpGet]
        public async Task<IActionResult> BlackholeAsync(string indexerId, string path, string jackett_apikey, string file)
        {
            var jsonReply = new JObject();
            try
            {
                if (_serverConfig.APIKey != jackett_apikey)
                    return Unauthorized();

                var indexer = _indexerService.GetWebIndexer(indexerId);
                if (!indexer.IsConfigured)
                {
                    _logger.Warn($"Rejected a request to {indexer.Name} which is unconfigured.");
                    throw new Exception("This indexer is not configured.");
                }

                path = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(path));
                path = _protectionService.UnProtect(path);
                var remoteFile = new Uri(path, UriKind.RelativeOrAbsolute);
                var fileExtension = ".torrent";

                byte[] downloadBytes;
                if (remoteFile.OriginalString.StartsWith("magnet"))
                    downloadBytes = Encoding.UTF8.GetBytes(remoteFile.OriginalString);
                else
                    downloadBytes = await indexer.Download(remoteFile);

                // handle magnet URLs
                if (downloadBytes.Length >= 7
                    && downloadBytes[0] == 0x6d // m
                    && downloadBytes[1] == 0x61 // a
                    && downloadBytes[2] == 0x67 // g
                    && downloadBytes[3] == 0x6e // n
                    && downloadBytes[4] == 0x65 // e
                    && downloadBytes[5] == 0x74 // t
                    && downloadBytes[6] == 0x3a // :
                    )
                {
                    fileExtension = ".magnet";
                }

                if (string.IsNullOrWhiteSpace(_serverConfig.BlackholeDir))
                {
                    throw new Exception("Blackhole directory not set!");
                }

                if (!Directory.Exists(_serverConfig.BlackholeDir))
                {
                    throw new Exception($"Blackhole directory does not exist: {_serverConfig.BlackholeDir}");
                }

                var fileName = DateTime.Now.Ticks.ToString() + "-" + StringUtil.MakeValidFileName(indexer.Name, '_', false);
                if (string.IsNullOrWhiteSpace(file))
                    fileName += fileExtension;
                else
                    fileName += "-" + StringUtil.MakeValidFileName(file + fileExtension, '_', false); // call MakeValidFileName() again to avoid any possibility of path traversal attacks

                try
                {
                    System.IO.File.WriteAllBytes(Path.Combine(_serverConfig.BlackholeDir, fileName), downloadBytes);
                }
                catch (IOException)
                {
                    // Sometimes a torrent's name is very long which causes an exception when writing the file to disk.
                    // In this specific case, use a GUID instead of the torrent's name.
                    System.IO.File.WriteAllBytes(Path.Combine(_serverConfig.BlackholeDir, Guid.NewGuid() + fileExtension), downloadBytes);
                }

                jsonReply["result"] = "success";
            }
            catch (Exception e)
            {
                _logger.Error($"Error downloading to blackhole. " +
                              $"indexer: {indexerId.Replace(Environment.NewLine, "")} " +
                              $"path: {path.Replace(Environment.NewLine, "")}\n{e}");
                jsonReply["result"] = "error";
                jsonReply["error"] = e.Message;
            }

            return Json(jsonReply);
        }
    }
}
