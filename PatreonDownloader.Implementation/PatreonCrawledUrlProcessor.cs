﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using PatreonDownloader.Implementation.Enums;
using PatreonDownloader.Implementation.Interfaces;
using PatreonDownloader.Implementation.Models;
using UniversalDownloaderPlatform.Common.Enums;
using UniversalDownloaderPlatform.Common.Exceptions;
using UniversalDownloaderPlatform.Common.Helpers;
using UniversalDownloaderPlatform.Common.Interfaces;
using UniversalDownloaderPlatform.Common.Interfaces.Models;

namespace PatreonDownloader.Implementation
{
    class PatreonCrawledUrlProcessor : ICrawledUrlProcessor
    {
        private static readonly HashSet<char> _invalidFilenameCharacters;

        private readonly IRemoteFilenameRetriever _remoteFilenameRetriever;
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private ConcurrentDictionary<string, int> _fileCountDict; //file counter for duplicate check
        private PatreonDownloaderSettings _patreonDownloaderSettings;

        private static readonly Regex _fileIdRegex = new Regex(
                    "https:\\/\\/(.+)\\.patreonusercontent\\.com\\/(.+)\\/patreon-media\\/p\\/post\\/([0-9]+)\\/([a-z0-9]+)",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase); //Regex used to retrieve file id from its url

        static PatreonCrawledUrlProcessor()
        {
            _invalidFilenameCharacters = new HashSet<char>(Path.GetInvalidFileNameChars());
            _invalidFilenameCharacters.Add(':');
        }

        public PatreonCrawledUrlProcessor(IRemoteFilenameRetriever remoteFilenameRetriever)
        {
            _remoteFilenameRetriever = remoteFilenameRetriever ??
                                       throw new ArgumentNullException(nameof(remoteFilenameRetriever));

            _logger.Debug("KemonoCrawledUrlProcessor initialized");
        }

        public async Task BeforeStart(IUniversalDownloaderPlatformSettings settings)
        {
            _fileCountDict = new ConcurrentDictionary<string, int>();
            _patreonDownloaderSettings = (PatreonDownloaderSettings) settings;
            await _remoteFilenameRetriever.BeforeStart(settings);
        }

        public async Task<bool> ProcessCrawledUrl(ICrawledUrl udpCrawledUrl)
        {
            PatreonCrawledUrl crawledUrl = (PatreonCrawledUrl)udpCrawledUrl;

            string downloadDirectory = "";

            if (_patreonDownloaderSettings.IsUseSubDirectories &&
                crawledUrl.UrlType != PatreonCrawledUrlType.AvatarFile &&
                crawledUrl.UrlType != PatreonCrawledUrlType.CoverFile)
                downloadDirectory = PostSubdirectoryHelper.CreateNameFromPattern(crawledUrl, _patreonDownloaderSettings.SubDirectoryPattern, _patreonDownloaderSettings.MaxSubdirectoryNameLength);

            if (crawledUrl.Url.IndexOf("youtube.com/watch?v=", StringComparison.Ordinal) != -1 ||
                     crawledUrl.Url.IndexOf("youtu.be/", StringComparison.Ordinal) != -1)
            {
                try
                {
                    _logger.Debug($"[{crawledUrl.PostId}] YOUTUBE link found: {crawledUrl.Url}");

                    bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                    var youtubeProcess = new Process
                    {
                        StartInfo =
                        {
                            FileName = $"yt-dlp{(isWindows ? ".exe" : "")}",
                            Arguments = $"{crawledUrl.Url} --format mp4 -o \"{downloadDirectory}/%(title)s.%(ext)s\"",
                            UseShellExecute = false,
                            CreateNoWindow = false,
                            RedirectStandardOutput = false
                        }
                    };
                    youtubeProcess.Start();

                    // Make sure app has finished the work
                    youtubeProcess.WaitForExit();
                }
                catch (Exception ex)
                {
                    _logger.Fatal($"[{crawledUrl.PostId}] Failed to download: {crawledUrl.Url}: {ex}");
                }
                return false; // downloading is handled here
            }
            else if (crawledUrl.Url.IndexOf("imgur.com/", StringComparison.Ordinal) != -1)
            {
                //TODO: IMGUR SUPPORT
                _logger.Fatal($"[{crawledUrl.PostId}] [NOT SUPPORTED] IMGUR link found: {crawledUrl.Url}");
                return false;
            }

            string filename = crawledUrl.Filename;

            if (!crawledUrl.IsProcessedByPlugin)
            {
                if (!_patreonDownloaderSettings.IsUseSubDirectories)
                    filename = $"{crawledUrl.PostId}_";
                else
                    filename = "";

                switch (crawledUrl.UrlType)
                {
                    case PatreonCrawledUrlType.PostFile:
                        filename += "post";
                        break;
                    case PatreonCrawledUrlType.PostAttachment:
                        filename += $"attachment";
                        if (!_patreonDownloaderSettings.IsUseLegacyFilenaming)
                            filename += $"_{crawledUrl.FileId}";
                        break;
                    case PatreonCrawledUrlType.PostMedia:
                        filename += $"media";
                        if (!_patreonDownloaderSettings.IsUseLegacyFilenaming)
                            filename += $"_{crawledUrl.FileId}";
                        break;
                    case PatreonCrawledUrlType.AvatarFile:
                        filename += "avatar";
                        break;
                    case PatreonCrawledUrlType.CoverFile:
                        filename += "cover";
                        break;
                    case PatreonCrawledUrlType.ExternalUrl:
                        filename += "external";
                        break;
                    default:
                        throw new ArgumentException($"Invalid url type: {crawledUrl.UrlType}");
                }

                if (crawledUrl.Filename == null)
                {
                    _logger.Debug($"No filename for {crawledUrl.Url}, trying to retrieve...");
                    string remoteFilename =
                        await _remoteFilenameRetriever.RetrieveRemoteFileName(crawledUrl.Url);

                    if (remoteFilename == null)
                    {
                        throw new DownloadException(
                            $"[{crawledUrl.PostId}] Unable to retrieve name for external entry of type {crawledUrl.UrlType}: {crawledUrl.Url}");
                    }

                    filename += $"_{remoteFilename}";
                }
                else
                {
                    filename += $"_{crawledUrl.Filename}";
                }

                _logger.Debug($"Filename for {crawledUrl.Url} is {filename}");

                _logger.Debug($"Sanitizing filename: {filename}");
                filename = PathSanitizer.SanitizePath(filename);
                _logger.Debug($"Sanitized filename: {filename}");

                if (filename.Length > _patreonDownloaderSettings.MaxFilenameLength)
                {
                    _logger.Debug($"Filename is too long, will be truncated: {filename}");
                    string extension = Path.GetExtension(filename);
                    if (extension.Length > 4)
                    {
                        _logger.Warn($"File extension for file {filename} is longer 4 characters and won't be appended to truncated filename!");
                        extension = "";
                    }
                    filename = filename.Substring(0, _patreonDownloaderSettings.MaxFilenameLength) + extension;
                    _logger.Debug($"Truncated filename: {filename}");
                }

                string key = $"{crawledUrl.PostId}_{filename.ToLowerInvariant()}";
                int fileCount = 1;

                _fileCountDict.AddOrUpdate(key, 1, (key, oldValue) =>
                {
                    fileCount = oldValue + 1;
                    return oldValue + 1;
                });

                if (fileCount > 1)
                {
                    _logger.Warn($"Found more than a single file with the name {filename} in the same folder in post {crawledUrl.PostId}, sequential number will be appended to its name.");

                    string appendStr = fileCount.ToString();

                    // External files captured by plugin may also Patreon files 
                    MatchCollection matches = _fileIdRegex.Matches(crawledUrl.Url);

                    if (crawledUrl.UrlType != PatreonCrawledUrlType.ExternalUrl)
                    {
                        if (matches.Count == 0)
                            throw new DownloadException($"[{crawledUrl.PostId}] Unable to retrieve file id for {crawledUrl.Url}, contact developer!");
                        if (matches.Count > 1)
                            throw new DownloadException($"[{crawledUrl.PostId}] More than 1 media found in URL {crawledUrl.Url}");
                    }

                    // If we get file id successfully
                    if (matches.Count == 1)
                    {
                        appendStr = matches[0].Groups[4].Value;
                    }

                    filename = $"{Path.GetFileNameWithoutExtension(filename)}_{appendStr}{Path.GetExtension(filename)}";
                }
            }

            crawledUrl.DownloadPath = !crawledUrl.IsProcessedByPlugin ? Path.Combine(downloadDirectory, filename) : downloadDirectory;

            return true;
        }
    }
}
