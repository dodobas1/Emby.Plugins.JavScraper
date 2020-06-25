﻿using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using System.Collections.Generic;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
using System.Linq;
using System.IO;
using MediaBrowser.Controller.Entities.Movies;
using Emby.Plugins.JavScraper.Configuration;

#if __JELLYFIN__
using Microsoft.Extensions.Logging;
#else

using MediaBrowser.Model.Logging;

#endif

#if DEBUG

namespace Emby.Plugins.JavScraper
{
    public class JavOrganizeTask : IScheduledTask
    {
        public string Name { get; } = "JavOrganize";
        public string Key { get; } = "JavOrganize";
        public string Description { get; } = "JavOrganize";
        public string Category => "Library";

        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths appPaths;
        private readonly IProviderManager providerManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public JavOrganizeTask(
#if __JELLYFIN__
            ILoggerFactory logManager
#else
            ILogManager logManager
#endif
            , ILibraryManager libraryManager, IJsonSerializer _jsonSerializer, IApplicationPaths appPaths,
            IProviderManager providerManager,
            ILibraryMonitor libraryMonitor,
            IFileSystem fileSystem)
        {
            _logger = logManager.CreateLogger<JavOrganizeTask>();
            this._libraryManager = libraryManager;
            this._jsonSerializer = _jsonSerializer;
            this.appPaths = appPaths;
            this.providerManager = providerManager;
            this.libraryMonitor = libraryMonitor;
            this._fileSystem = fileSystem;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => new TaskTriggerInfo[] { };

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            _logger.Info($"Running...");
            progress.Report(0);

            var options = Plugin.Instance.Configuration?.JavOrganizationOptions;
            var empty = Plugin.Instance.Configuration?.TitleFormatEmptyValue;

            if (options?.WatchLocations?.Any() != true && string.IsNullOrWhiteSpace(options.WatchLocations[0]))
            {
                _logger.Warn("source folder cannot be empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(options?.TargetLocation))
            {
                _logger.Warn("target folder is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(options.MovieFolderPattern) && string.IsNullOrWhiteSpace(options.MoviePattern))
            {
                _logger.Warn("folder pattern and file name pattern cannot be empty at the same time.");
                return;
            }

            var libraryFolderPaths = _libraryManager.GetVirtualFolders()
                .Where(dir => dir.CollectionType == "movies" && dir.Locations?.Any() == true &&
                    dir.LibraryOptions.TypeOptions?.Any(o => o.MetadataFetchers?.Contains(Plugin.NAME) == true) == true)
                .SelectMany(o => o.Locations).ToList();

            var watchLocations = options.WatchLocations
                .Where(o => IsValidWatchLocation(o, libraryFolderPaths))
                .ToList();

            var eligibleFiles = watchLocations.SelectMany(GetFilesToOrganize)
                .OrderBy(_fileSystem.GetCreationTimeUtc)
                .Where(i => EnableOrganization(i, options))
                .ToList();

            var processedFolders = new HashSet<string>();

            _logger.Info($"{eligibleFiles.Count} files found");
            if (eligibleFiles.Count == 0)
            {
                progress.Report(100);
                return;
            }

            int index = 0;
            foreach (var m in eligibleFiles)
            {
                progress.Report(index * 1.0 / eligibleFiles.Count * 100);
                index++;

                var movie = _libraryManager.FindByPath(m.FullName, false) as Movie;
                if (movie == null)
                {
                    _logger.Error($"the movie does not exists. {m.FullName}");
                    continue;
                }

                var jav = movie.GetJavVideoIndex(_jsonSerializer);
                if (jav == null)
                {
                    _logger.Error($"jav video index does not exists. {m.FullName}");
                    continue;
                }

                if (jav.Genres == null || jav.Actors == null)
                {
                    var l = jav.LoadFromCache(appPaths.CachePath, _jsonSerializer);
                    if (l != null)
                        jav = l;
                }

                //1，文件名中可能包含路径，
                //2，去除路径中非法字符
                //3，路径分隔符
                //4，文件夹或者文件名中包含-C/-C2 中文字幕
                //5，移动以文件名开通的文件
                //6，移动某些特定文件名的文件
                //7，替换nfo文件内的路径
                //8，复制nfo中的其他文件?

                var has_chinese_subtitle = movie.Genres?.Contains("中文字幕") == true;
                if (has_chinese_subtitle == false)
                {
                    var arr = new[] { Path.GetFileNameWithoutExtension(m.FullName), Path.GetFileName(Path.GetDirectoryName(m.FullName)) };
                    var cc = new[] { "-C", "-C2", "_C", "_C2" };
                    has_chinese_subtitle = arr.Any(v => cc.Any(x => v.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
                }

                var target_dir = options.TargetLocation;
                if (string.IsNullOrWhiteSpace(options.MovieFolderPattern) == false)
                    target_dir = Path.Combine(target_dir, jav.GetFormatName(options.MovieFolderPattern, empty, true));
                string name = null;
                if (string.IsNullOrWhiteSpace(options.MoviePattern) == false)
                {
                    //文件名部分
                    name = jav.GetFormatName(options.MoviePattern, empty, true);
                }
                else
                {
                    name = Path.GetFileName(target_dir);
                    target_dir = Path.GetDirectoryName(target_dir);
                }
                //文件名（含扩展名）
                var filename = name + Path.GetExtension(m.FullName);
                //目标全路径
                var full = Path.GetFullPath(Path.Combine(target_dir, filename));

                //文件名中可能包含路基，所以需要重新计算文件名
                filename = Path.GetFileName(full);
                name = Path.GetFileNameWithoutExtension(filename);
                target_dir = Path.GetDirectoryName(full);

                if (has_chinese_subtitle && options.AddChineseSubtitleSuffix >= 1 && options.AddChineseSubtitleSuffix <= 3) //中文字幕
                {
                    if (options.AddChineseSubtitleSuffix == 1 || options.AddChineseSubtitleSuffix == 3)
                        //包含在文件夹中
                        target_dir += "-C";
                    if (options.AddChineseSubtitleSuffix == 2 || options.AddChineseSubtitleSuffix == 3)
                        //包含在文件名中
                        name += "-C";
                    filename = name + Path.GetExtension(filename);
                    full = Path.GetFullPath(Path.Combine(target_dir, filename));
                }

                if (_fileSystem.DirectoryExists(target_dir) == false)
                    _fileSystem.CreateDirectory(target_dir);

                //老的文件名
                var source_name = Path.GetFileNameWithoutExtension(m.FullName);
                var source_dir = Path.GetDirectoryName(m.FullName);

                //已经存在的就跳过
                if (options.OverwriteExistingFiles == false && _fileSystem.FileExists(full))
                {
                    _logger.Error($"FileExists: {full}");
                    continue;
                }
                var source_files = _fileSystem.GetFiles(source_dir);
                var fss = new List<(string from, string to)>();
                foreach (var f in source_files.Select(o => o.FullName))
                {
                    var n = Path.GetFileName(f);
                    if (n.StartsWith(source_name, StringComparison.OrdinalIgnoreCase))
                    {
                        n = name + n.Substring(source_name.Length);
                        fss.Add((f, Path.Combine(target_dir, n)));
                    }
                    else if (n.StartsWith("fanart", StringComparison.OrdinalIgnoreCase) || n.StartsWith("poster", StringComparison.OrdinalIgnoreCase))
                        fss.Add((f, Path.Combine(target_dir, n)));
                }

                foreach (var f in fss)
                {
                    if (options.OverwriteExistingFiles == false && _fileSystem.FileExists(f.to))
                    {
                        _logger.Info($"FileSkip: {f.from} {f.to}");
                        continue;
                    }

                    if (options.CopyOriginalFile)
                    {
                        _fileSystem.CopyFile(f.from, f.to, options.OverwriteExistingFiles);
                        _logger.Info($"FileCopy: {f.from} {f.to}");
                    }
                    else
                    {
                        _fileSystem.MoveFile(f.from, f.to);
                        _logger.Info($"FileMove: {f.from} {f.to}");
                    }
                }

                //var source_extrafanart = Path.Combine(source_dir, "extrafanart");
                //var target_extrafanart = Path.Combine(target_dir, "extrafanart");
                //if (Directory.Exists(source_extrafanart) && (options.OverwriteExistingFiles || !Directory.Exists(target_extrafanart)))
                //{
                //    if (options.CopyOriginalFile)
                //    {
                //        fileSystem.CopyFile(source_extrafanart, target_extrafanart,options.OverwriteExistingFiles);
                //        _logger.Info($"DirectoryCopy: {source_extrafanart} {target_extrafanart}");
                //    }
                //    else
                //    {
                //        fileSystem.MoveDirectory(source_extrafanart, target_extrafanart);
                //        _logger.Info($"DirectoryMove: {source_extrafanart} {target_extrafanart}");
                //    }
                //}

                //更新 nfo 文件
                foreach (var nfo in fss.Where(o => o.to.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)).Select(o => o.to))
                {
                    var txt = File.ReadAllText(nfo);
                    if (txt.IndexOf(source_dir) >= 0)
                    {
                        txt = txt.Replace(source_dir, target_dir);
                        File.WriteAllText(nfo, txt);
                    }
                }
                movie.Path = full;
                movie.UpdateToRepository(ItemUpdateType.MetadataImport);
                if (!processedFolders.Contains(m.DirectoryName, StringComparer.OrdinalIgnoreCase))
                    processedFolders.Add(m.DirectoryName);
            }
            progress.Report(99);

            var deleteExtensions = options.LeftOverFileExtensionsToDelete
                .Select(e => e.Trim().TrimStart('.'))
                .Where(e => !string.IsNullOrEmpty(e))
                .Select(e => "." + index)
                .ToList();

            Clean(processedFolders, watchLocations, options.DeleteEmptyFolders, deleteExtensions);

            // Extended Clean
            if (options.ExtendedClean)
            {
                Clean(watchLocations, watchLocations, options.DeleteEmptyFolders, deleteExtensions);
            }
            progress.Report(100);
        }

        private bool EnableOrganization(FileSystemMetadata fileInfo, JavOrganizationOptions options)
        {
            var minFileBytes = options.MinFileSizeMb * 1024 * 1024;

            try
            {
                return _libraryManager.IsVideoFile(fileInfo.FullName.AsSpan()) && fileInfo.Length >= minFileBytes;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error organizing file {0}", ex, fileInfo.Name);
            }

            return false;
        }

        private void Clean(IEnumerable<string> paths, List<string> watchLocations, bool deleteEmptyFolders, List<string> deleteExtensions)
        {
            foreach (var path in paths)
            {
                if (deleteExtensions.Count > 0)
                {
                    DeleteLeftOverFiles(path, deleteExtensions);
                }

                if (deleteEmptyFolders)
                {
                    DeleteEmptyFolders(path, watchLocations);
                }
            }
        }

        /// <summary>
        /// Deletes the left over files.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="extensions">The extensions.</param>
        private void DeleteLeftOverFiles(string path, IEnumerable<string> extensions)
        {
            var eligibleFiles = _fileSystem.GetFilePaths(path, extensions.ToArray(), false, true)
                .ToList();

            foreach (var file in eligibleFiles)
            {
                try
                {
                    _fileSystem.DeleteFile(file);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error deleting file {0}", ex, file);
                }
            }
        }

        /// <summary>
        /// Deletes the empty folders.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="watchLocations">The path.</param>
        private void DeleteEmptyFolders(string path, List<string> watchLocations)
        {
            try
            {
                foreach (var d in _fileSystem.GetDirectoryPaths(path))
                {
                    DeleteEmptyFolders(d, watchLocations);
                }

                var entries = _fileSystem.GetFileSystemEntryPaths(path);

                if (!entries.Any() && !IsWatchFolder(path, watchLocations))
                {
                    try
                    {
                        _logger.Debug("Deleting empty directory {0}", path);
                        _fileSystem.DeleteDirectory(path, false);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Determines if a given folder path is contained in a folder list
        /// </summary>
        /// <param name="path">The folder path to check.</param>
        /// <param name="watchLocations">A list of folders.</param>
        private bool IsWatchFolder(string path, IEnumerable<string> watchLocations)
        {
            return watchLocations.Contains(path, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsValidWatchLocation(string path, List<string> libraryFolderPaths)
        {
            if (IsPathAlreadyInMediaLibrary(path, libraryFolderPaths))
            {
                _logger.Info("Folder {0} is not eligible for auto-organize because it is also part of an Emby library", path);
                return false;
            }

            return true;
        }

        private bool IsPathAlreadyInMediaLibrary(string path, List<string> libraryFolderPaths)
        {
            return libraryFolderPaths.Any(i => string.Equals(i, path, StringComparison.Ordinal) || _fileSystem.ContainsSubPath(i.AsSpan(), path.AsSpan()));
        }

        /// <summary>
        /// Gets the files to organize.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>IEnumerable{FileInfo}.</returns>
        private List<FileSystemMetadata> GetFilesToOrganize(string path)
        {
            try
            {
                return _fileSystem.GetFiles(path, true)
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                _logger.Info("Auto-Organize watch folder does not exist: {0}", path);

                return new List<FileSystemMetadata>();
            }
            catch (IOException ex)
            {
                _logger.ErrorException("Error getting files from {0}", ex, path);

                return new List<FileSystemMetadata>();
            }
        }
    }
}

#endif