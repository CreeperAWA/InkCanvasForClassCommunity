using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Ink_Canvas.Helpers
{
    internal class AutoUpdateHelper
    {
        // 定义超时时间为10秒
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly string updatesFolderPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "AutoUpdate");
        private static string statusFilePath;

        // 线路组结构体（包含版本、下载、日志地址）
        public class UpdateLineGroup
        {
            public string GroupName { get; set; } // 组名
            public string VersionUrl { get; set; } // 版本检测地址
            public string DownloadUrlFormat { get; set; } // 下载地址格式（带{0}占位符）
            public string LogUrl { get; set; } // 更新日志地址
        }

        // 通道-线路组映射
        public static readonly Dictionary<UpdateChannel, List<UpdateLineGroup>> ChannelLineGroups = new Dictionary<UpdateChannel, List<UpdateLineGroup>>
        {
            { UpdateChannel.Release, new List<UpdateLineGroup>
                {
                    new UpdateLineGroup
                    {
                        GroupName = "GitHub主线",
                        VersionUrl = "https://github.com/InkCanvasForClass/community/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://github.com/InkCanvasForClass/community/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://github.com/InkCanvasForClass/community/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "bgithub备用",
                        VersionUrl = "https://bgithub.xyz/InkCanvasForClass/community/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://bgithub.xyz/InkCanvasForClass/community/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://bgithub.xyz/InkCanvasForClass/community/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "kkgithub线路",
                        VersionUrl = "https://kkgithub.com/InkCanvasForClass/community/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://kkgithub.com/InkCanvasForClass/community/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://kkgithub.com/InkCanvasForClass/community/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "智教联盟",
                        DownloadUrlFormat = "https://get.smart-teach.cn/d/Ningbo-S3/shared/jiangling/community/InkCanvasForClass.CE.{0}.zip",
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "inkeys",
                        DownloadUrlFormat = "https://iccce.inkeys.top/Release/InkCanvasForClass.CE.{0}.zip",
                    }
                }
            },
            { UpdateChannel.Beta, new List<UpdateLineGroup>
                {
                    new UpdateLineGroup
                    {
                        GroupName = "GitHub主线",
                        VersionUrl = "https://github.com/InkCanvasForClass/community-beta/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://github.com/InkCanvasForClass/community-beta/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://github.com/InkCanvasForClass/community-beta/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "bgithub备用",
                        VersionUrl = "https://bgithub.xyz/InkCanvasForClass/community-beta/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://bgithub.xyz/InkCanvasForClass/community-beta/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://bgithub.xyz/InkCanvasForClass/community-beta/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "kkgithub线路",
                        VersionUrl = "https://kkgithub.com/InkCanvasForClass/community-beta/raw/refs/heads/main/AutomaticUpdateVersionControl.txt",
                        DownloadUrlFormat = "https://kkgithub.com/InkCanvasForClass/community-beta/releases/download/{0}/InkCanvasForClass.CE.{0}.zip",
                        LogUrl = "https://kkgithub.com/InkCanvasForClass/community-beta/raw/refs/heads/main/UpdateLog.md"
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "智教联盟",
                        DownloadUrlFormat = "https://get.smart-teach.cn/d/Ningbo-S3/shared/jiangling/community-beta/InkCanvasForClass.CE.{0}.zip",
                    },
                    new UpdateLineGroup
                    {
                        GroupName = "inkeys",
                        DownloadUrlFormat = "https://iccce.inkeys.top/Beta/InkCanvasForClass.CE.{0}.zip",
                    }
                }
            }
        };

        // 区块任务结构体（移到类体内）
        private class BlockTask
        {
            public int Index;
            public long Start;
            public long End;
            public int RetryCount;
        }

        // 检测URL延迟
        private static async Task<long> GetUrlDelay(string url)
        {
            try
            {
                // 检测是否为Windows 7
                var osVersion = Environment.OSVersion;
                bool isWindows7 = osVersion.Version.Major == 6 && osVersion.Version.Minor == 1;

                if (isWindows7)
                {
                    // Windows 7使用特殊配置
                    using (var handler = new HttpClientHandler())
                    {
                        // 配置HttpClientHandler以支持Windows 7
                        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                        using (var client = new HttpClient(handler))
                        {
                            client.Timeout = TimeSpan.FromSeconds(5);
                            var sw = Stopwatch.StartNew();
                            var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                            sw.Stop();
                            if (resp.IsSuccessStatusCode)
                                return sw.ElapsedMilliseconds;
                        }
                    }
                }
                else
                {
                    // 其他Windows版本使用标准配置
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(5);
                        var sw = Stopwatch.StartNew();
                        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
                        sw.Stop();
                        if (resp.IsSuccessStatusCode)
                            return sw.ElapsedMilliseconds;
                    }
                }
            }
            catch { }
            return -1;
        }

        // 检测线路组延迟，返回最快组（保持向后兼容）
        private static async Task<UpdateLineGroup> GetFastestLineGroup(UpdateChannel channel)
        {
            var availableGroups = await GetAvailableLineGroupsOrdered(channel);
            return availableGroups.Count > 0 ? availableGroups[0] : null;
        }

        // 获取所有可用线路组，按延迟排序
        public static async Task<List<UpdateLineGroup>> GetAvailableLineGroupsOrdered(UpdateChannel channel)
        {
            var groups = ChannelLineGroups[channel];
            var availableGroups = new List<(UpdateLineGroup group, long delay)>();

            LogHelper.WriteLogToFile($"AutoUpdate | 开始检测通道 {channel} 下所有线路组延迟...");

            foreach (var group in groups)
            {
                // 跳过"智教联盟"和"inkeys"线路组，不参与延迟检测和排序
                if (group.GroupName == "智教联盟" || group.GroupName == "inkeys")
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 跳过{group.GroupName}线路组延迟检测");
                    continue;
                }
                LogHelper.WriteLogToFile($"AutoUpdate | 检测线路组: {group.GroupName} ({group.VersionUrl})");
                var delay = await GetUrlDelay(group.VersionUrl);
                if (delay >= 0)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 线路组 {group.GroupName} 延迟: {delay}ms");
                    availableGroups.Add((group, delay));
                }
                else
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 线路组 {group.GroupName} 不可用", LogHelper.LogType.Warning);
                }
            }

            // 按延迟排序，延迟最小的排在前面
            var orderedGroups = availableGroups
                .OrderBy(x => x.delay)
                .Select(x => x.group)
                .ToList();

            // 将"智教联盟"线路组插入到最前面（如果存在）
            var zhiJiaoGroup = groups.FirstOrDefault(g => g.GroupName == "智教联盟");
            if (zhiJiaoGroup != null)
            {
                orderedGroups.Insert(0, zhiJiaoGroup);
                LogHelper.WriteLogToFile("AutoUpdate | 智教联盟线路组已插入到首位");
            }

            // 将"inkeys"线路组插入到第二位（如果存在）
            var inkeysGroup = groups.FirstOrDefault(g => g.GroupName == "inkeys");
            if (inkeysGroup != null)
            {
                orderedGroups.Insert(1, inkeysGroup);
                LogHelper.WriteLogToFile("AutoUpdate | inkeys线路组已插入到第二位");
            }

            if (orderedGroups.Count > 0)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 找到 {orderedGroups.Count} 个可用线路组，按延迟排序:");
                for (int i = 0; i < orderedGroups.Count; i++)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | {i + 1}. {orderedGroups[i].GroupName}");
                }
            }
            else
            {
                LogHelper.WriteLogToFile("AutoUpdate | 所有线路组均不可用", LogHelper.LogType.Error);
            }

            return orderedGroups;
        }

        // 获取远程版本号
        private static async Task<string> GetRemoteVersion(string fileUrl)
        {
            // 检测是否为Windows 7
            var osVersion = Environment.OSVersion;
            bool isWindows7 = osVersion.Version.Major == 6 && osVersion.Version.Minor == 1;

            if (isWindows7)
            {
                // Windows 7使用特殊配置
                using (var handler = new HttpClientHandler())
                {
                    try
                    {
                        // 配置HttpClientHandler以支持Windows 7
                        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                        using (HttpClient client = new HttpClient(handler))
                        {
                            client.Timeout = RequestTimeout;
                            LogHelper.WriteLogToFile($"AutoUpdate | 发送HTTP请求到: {fileUrl}");

                            var downloadTask = client.GetAsync(fileUrl);
                            var timeoutTask = Task.Delay(RequestTimeout);

                            var completedTask = await Task.WhenAny(downloadTask, timeoutTask);
                            if (completedTask == timeoutTask)
                            {
                                LogHelper.WriteLogToFile($"AutoUpdate | 请求超时 ({RequestTimeout.TotalSeconds}秒)", LogHelper.LogType.Error);
                                return null;
                            }

                            HttpResponseMessage response = await downloadTask;
                            LogHelper.WriteLogToFile($"AutoUpdate | HTTP响应状态: {response.StatusCode}");
                            response.EnsureSuccessStatusCode();

                            string content = await response.Content.ReadAsStringAsync();
                            content = content.Trim();

                            // 如果内容包含HTML（可能是GitHub页面而不是原始内容），尝试提取版本号
                            if (content.Contains("<html") || content.Contains("<!DOCTYPE"))
                            {
                                LogHelper.WriteLogToFile("AutoUpdate | 收到HTML内容而不是原始版本号 - 尝试提取版本");
                                int startPos = content.IndexOf("<table");
                                if (startPos > 0)
                                {
                                    int endPos = content.IndexOf("</table>", startPos);
                                    if (endPos > startPos)
                                    {
                                        string tableContent = content.Substring(startPos, endPos - startPos);
                                        var match = Regex.Match(tableContent, @"(\d+\.\d+\.\d+(\.\d+)?)");
                                        if (match.Success)
                                        {
                                            content = match.Groups[1].Value;
                                            LogHelper.WriteLogToFile($"AutoUpdate | 从HTML提取版本: {content}");
                                        }
                                        else
                                        {
                                            LogHelper.WriteLogToFile("AutoUpdate | 无法从HTML内容提取版本");
                                            return null;
                                        }
                                    }
                                }
                            }

                            LogHelper.WriteLogToFile($"AutoUpdate | 响应内容: {content}");
                            return content;
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | HTTP请求错误: {ex.Message}", LogHelper.LogType.Error);
                    }
                    catch (TaskCanceledException ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 请求超时: {ex.Message}", LogHelper.LogType.Error);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 错误: {ex.Message}", LogHelper.LogType.Error);
                    }

                    return null;
                }
            }

            // 其他Windows版本使用标准配置
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    client.Timeout = RequestTimeout;
                    LogHelper.WriteLogToFile($"AutoUpdate | 发送HTTP请求到: {fileUrl}");

                    var downloadTask = client.GetAsync(fileUrl);
                    var timeoutTask = Task.Delay(RequestTimeout);

                    var completedTask = await Task.WhenAny(downloadTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 请求超时 ({RequestTimeout.TotalSeconds}秒)", LogHelper.LogType.Error);
                        return null;
                    }

                    HttpResponseMessage response = await downloadTask;
                    LogHelper.WriteLogToFile($"AutoUpdate | HTTP响应状态: {response.StatusCode}");
                    response.EnsureSuccessStatusCode();

                    string content = await response.Content.ReadAsStringAsync();
                    content = content.Trim();

                    // 如果内容包含HTML（可能是GitHub页面而不是原始内容），尝试提取版本号
                    if (content.Contains("<html") || content.Contains("<!DOCTYPE"))
                    {
                        LogHelper.WriteLogToFile("AutoUpdate | 收到HTML内容而不是原始版本号 - 尝试提取版本");
                        int startPos = content.IndexOf("<table");
                        if (startPos > 0)
                        {
                            int endPos = content.IndexOf("</table>", startPos);
                            if (endPos > startPos)
                            {
                                string tableContent = content.Substring(startPos, endPos - startPos);
                                var match = Regex.Match(tableContent, @"(\d+\.\d+\.\d+(\.\d+)?)");
                                if (match.Success)
                                {
                                    content = match.Groups[1].Value;
                                    LogHelper.WriteLogToFile($"AutoUpdate | 从HTML提取版本: {content}");
                                }
                                else
                                {
                                    LogHelper.WriteLogToFile("AutoUpdate | 无法从HTML内容提取版本");
                                    return null;
                                }
                            }
                        }
                    }

                    LogHelper.WriteLogToFile($"AutoUpdate | 响应内容: {content}");
                    return content;
                }
                catch (HttpRequestException ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | HTTP请求错误: {ex.Message}", LogHelper.LogType.Error);
                }
                catch (TaskCanceledException ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 请求超时: {ex.Message}", LogHelper.LogType.Error);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 错误: {ex.Message}", LogHelper.LogType.Error);
                }

                return null;
            }
        }

        // 通过GitHub API获取指定版本的Release信息
        private static async Task<(string version, string downloadUrl, string releaseNotes, DateTime? releaseTime)> GetGithubReleaseByVersion(string targetVersion, UpdateChannel channel)
        {
            try
            {
                string apiUrl = channel == UpdateChannel.Beta
                    ? "https://api.github.com/repos/InkCanvasForClass/community-beta/releases"
                    : "https://api.github.com/repos/InkCanvasForClass/community/releases";
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ICC-CE Auto Updater");
                    var response = await client.GetStringAsync(apiUrl);
                    var releases = JArray.Parse(response);

                    foreach (var release in releases)
                    {
                        string version = release["tag_name"]?.ToString();
                        if (version == targetVersion || version == $"v{targetVersion}" || version == $"V{targetVersion}")
                        {
                            string releaseNotes = release["body"]?.ToString();
                            string downloadUrl = release["assets"]?.First?["browser_download_url"]?.ToString();

                            // 解析发布时间
                            DateTime? releaseTime = null;
                            if (release["published_at"] != null && DateTime.TryParse(release["published_at"].ToString(), out DateTime parsedTime))
                            {
                                releaseTime = parsedTime;
                            }

                            return (version, downloadUrl, releaseNotes, releaseTime);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | GitHub Releases API 获取版本 {targetVersion} 失败: {ex.Message}", LogHelper.LogType.Warning);
            }
            return (null, null, null, null);
        }

        // 通过GitHub API获取最新Release信息
        private static async Task<(string version, string downloadUrl, string releaseNotes, DateTime? releaseTime)> GetLatestGithubRelease(UpdateChannel channel)
        {
            try
            {
                string apiUrl = channel == UpdateChannel.Beta
                    ? "https://api.github.com/repos/InkCanvasForClass/community-beta/releases/latest"
                    : "https://api.github.com/repos/InkCanvasForClass/community/releases/latest";
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ICC-CE Auto Updater");
                    var response = await client.GetStringAsync(apiUrl);
                    var json = JObject.Parse(response);
                    string version = json["tag_name"]?.ToString();
                    string releaseNotes = json["body"]?.ToString();
                    string downloadUrl = json["assets"]?.First?["browser_download_url"]?.ToString();

                    // 解析发布时间
                    DateTime? releaseTime = null;
                    if (json["published_at"] != null && DateTime.TryParse(json["published_at"].ToString(), out DateTime parsedTime))
                    {
                        releaseTime = parsedTime;
                    }

                    if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(downloadUrl))
                        return (version, downloadUrl, releaseNotes, releaseTime);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | GitHub Releases API 获取失败: {ex.Message}", LogHelper.LogType.Warning);
            }
            return (null, null, null, null);
        }

        // 主要的更新检测方法（优先检测延迟，失败时自动切换线路组）
        // 仅检测新版本时用GitHub API，实际下载时只用线路组
        public static async Task<(string remoteVersion, UpdateLineGroup lineGroup, string releaseNotes)> CheckForUpdates(UpdateChannel channel = UpdateChannel.Release, bool alwaysGetRemote = false, bool isVersionFix = false)
        {
            try
            {
                // 记录更新检查时间
                DeviceIdentifier.RecordUpdateCheck();

                string localVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                LogHelper.WriteLogToFile($"AutoUpdate | 本地版本: {localVersion}");
                LogHelper.WriteLogToFile($"AutoUpdate | 设备ID: {DeviceIdentifier.GetDeviceId()}");
                LogHelper.WriteLogToFile($"AutoUpdate | 更新优先级: {DeviceIdentifier.GetUpdatePriority()}");
                LogHelper.WriteLogToFile("AutoUpdate | 优先通过GitHub Releases API检测...");

                // 1. 优先通过GitHub Releases API获取
                var (apiVersion, _, apiReleaseNotes, apiReleaseTime) = await GetLatestGithubRelease(channel);
                if (!string.IsNullOrEmpty(apiVersion))
                {
                    Version local = new Version(localVersion);
                    Version remote = new Version(apiVersion.TrimStart('v', 'V'));
                    if (remote > local || alwaysGetRemote)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 通过GitHub Releases API发现新版本: {apiVersion}");

                        // 检查是否应该根据用户优先级推送更新（版本修复功能不受限制）
                        if (!isVersionFix)
                        {
                            DateTime releaseTime = apiReleaseTime ?? DateTime.Now;

                            // 尝试获取当前版本的发布时间
                            DateTime? currentVersionReleaseTime = await GetVersionReleaseTime(localVersion, channel);

                            bool shouldPush = DeviceIdentifier.ShouldPushUpdate(apiVersion, releaseTime, true, currentVersionReleaseTime); // 明确标记为自动更新
                            if (!shouldPush)
                            {
                                var priority = DeviceIdentifier.GetUpdatePriority();
                                var daysBetweenVersions = currentVersionReleaseTime.HasValue
                                    ? (releaseTime - currentVersionReleaseTime.Value).TotalDays
                                    : (DateTime.Now - releaseTime).TotalDays;
                                LogHelper.WriteLogToFile($"AutoUpdate | 根据用户优先级({priority})，暂不推送更新 {apiVersion}，版本间隔: {daysBetweenVersions:F1} 天");
                                var group = (await GetAvailableLineGroupsOrdered(channel)).FirstOrDefault();
                                return (null, group, apiReleaseNotes); // 返回null表示不推送
                            }
                        }
                        else
                        {
                            LogHelper.WriteLogToFile("AutoUpdate | 版本修复模式，跳过分级策略检查");
                        }

                        LogHelper.WriteLogToFile($"AutoUpdate | 根据用户优先级，推送更新 {apiVersion}");
                        // 只返回版本号和日志，不返回直链
                        var availableGroup = (await GetAvailableLineGroupsOrdered(channel)).FirstOrDefault();
                        return (apiVersion, availableGroup, apiReleaseNotes);
                    }
                    else
                    {
                        LogHelper.WriteLogToFile("AutoUpdate | 当前版本已是最新 (GitHub Releases API)");
                        var availableGroup = (await GetAvailableLineGroupsOrdered(channel)).FirstOrDefault();
                        return (null, availableGroup, apiReleaseNotes);
                    }
                }
                // 2. 回退到原有txt方案
                LogHelper.WriteLogToFile("AutoUpdate | GitHub Releases API获取失败，回退到txt方案...");
                var availableGroups = await GetAvailableLineGroupsOrdered(channel);
                if (availableGroups.Count == 0)
                {
                    LogHelper.WriteLogToFile("AutoUpdate | 所有线路组均不可用", LogHelper.LogType.Error);
                    return (null, null, null);
                }
                foreach (var group in availableGroups)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 尝试使用线路组获取版本信息: {group.GroupName}");
                    string remoteVersion = await GetRemoteVersion(group.VersionUrl);
                    if (remoteVersion != null)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 成功从线路组 {group.GroupName} 获取远程版本: {remoteVersion}");
                        Version local = new Version(localVersion);
                        Version remote = new Version(remoteVersion);
                        if (remote > local || alwaysGetRemote)
                        {
                            LogHelper.WriteLogToFile($"AutoUpdate | 发现新版本或强制获取: {remoteVersion}");

                            // 检查是否应该根据用户优先级推送更新（版本修复功能不受限制）
                            if (!isVersionFix)
                            {
                                // 尝试获取当前版本的发布时间
                                DateTime? currentVersionReleaseTime = await GetVersionReleaseTime(localVersion, channel);

                                bool shouldPush = DeviceIdentifier.ShouldPushUpdate(remoteVersion, DateTime.Now, true, currentVersionReleaseTime); // 明确标记为自动更新
                                if (!shouldPush)
                                {
                                    var priority = DeviceIdentifier.GetUpdatePriority();
                                    LogHelper.WriteLogToFile($"AutoUpdate | 根据用户优先级({priority})，暂不推送更新 {remoteVersion}");
                                    return (null, group, null); // 返回null表示不推送
                                }
                            }
                            else
                            {
                                LogHelper.WriteLogToFile("AutoUpdate | 版本修复模式，跳过分级策略检查");
                            }

                            LogHelper.WriteLogToFile($"AutoUpdate | 根据用户优先级，推送更新 {remoteVersion}");
                            return (remoteVersion, group, null);
                        }

                        LogHelper.WriteLogToFile("AutoUpdate | 当前版本已是最新");
                        return (null, group, null);
                    }

                    LogHelper.WriteLogToFile($"AutoUpdate | 线路组 {group.GroupName} 获取版本失败，尝试下一个线路组", LogHelper.LogType.Warning);
                }
                LogHelper.WriteLogToFile("AutoUpdate | 所有线路组均无法获取版本信息", LogHelper.LogType.Error);
                return (null, null, null);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | CheckForUpdates错误: {ex.Message}", LogHelper.LogType.Error);
                return (null, null, null);
            }
        }

        // 使用指定线路组下载新版
        public static async Task<bool> DownloadSetupFile(string version, UpdateLineGroup group)
        {
            return await DownloadSetupFileWithFallback(version, new List<UpdateLineGroup> { group });
        }

        // 获取智教联盟真实下载地址
        private static async Task<string> GetZhijiaoRealDownloadUrl(string url)
        {
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = RequestTimeout;
                    var resp = await client.GetAsync(url);
                    // 优先取Location头
                    if (resp.StatusCode == HttpStatusCode.Found || resp.StatusCode == HttpStatusCode.Redirect || resp.StatusCode == HttpStatusCode.MovedPermanently)
                    {
                        if (resp.Headers.Location != null)
                        {
                            var realUrl = resp.Headers.Location.ToString();
                            if (realUrl.Contains(" ")) realUrl = realUrl.Replace(" ", "%20");
                            return realUrl;
                        }
                    }
                    // 有些服务器直接返回真实地址在内容里
                    var content = await resp.Content.ReadAsStringAsync();
                    if (Uri.IsWellFormedUriString(content.Trim(), UriKind.Absolute))
                    {
                        var realUrl = content.Trim();
                        if (realUrl.Contains(" ")) realUrl = realUrl.Replace(" ", "%20");
                        return realUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 获取智教联盟真实下载地址失败: {ex.Message}", LogHelper.LogType.Error);
            }
            return null;
        }

        // 使用多线路组下载新版（支持自动切换）
        public static async Task<bool> DownloadSetupFileWithFallback(string version, List<UpdateLineGroup> groups, Action<double, string> progressCallback = null)
        {
            try
            {
                statusFilePath = Path.Combine(updatesFolderPath, $"DownloadV{version}Status.txt");

                if (File.Exists(statusFilePath) && File.ReadAllText(statusFilePath).Trim().ToLower() == "true")
                {
                    LogHelper.WriteLogToFile("AutoUpdate | 安装包已下载");
                    progressCallback?.Invoke(100, "已下载完成");
                    return true;
                }

                // 确保更新目录存在
                if (!Directory.Exists(updatesFolderPath))
                {
                    Directory.CreateDirectory(updatesFolderPath);
                    LogHelper.WriteLogToFile($"AutoUpdate | 创建更新目录: {updatesFolderPath}");
                }

                string zipFilePath = Path.Combine(updatesFolderPath, $"InkCanvasForClass.CE.{version}.zip");
                LogHelper.WriteLogToFile($"AutoUpdate | 目标文件路径: {zipFilePath}");

                SaveDownloadStatus(false);

                // 优先尝试“智教联盟”线路组
                var zhiJiaoGroup = groups.FirstOrDefault(g => g.GroupName == "智教联盟");
                var inkeysGroup = groups.FirstOrDefault(g => g.GroupName == "inkeys");
                if (zhiJiaoGroup != null || inkeysGroup != null)
                {
                    var priorityGroups = new List<UpdateLineGroup>();
                    if (zhiJiaoGroup != null)
                    {
                        priorityGroups.Add(zhiJiaoGroup);
                        LogHelper.WriteLogToFile("AutoUpdate | 下载时优先尝试智教联盟线路组");
                    }
                    if (inkeysGroup != null)
                    {
                        priorityGroups.Add(inkeysGroup);
                        LogHelper.WriteLogToFile("AutoUpdate | 下载时优先尝试inkeys线路组");
                    }
                    groups = priorityGroups.Concat(groups.Where(g => g.GroupName != "智教联盟" && g.GroupName != "inkeys")).ToList();
                }

                // 依次尝试每个线路组
                foreach (var group in groups)
                {
                    string url = string.Format(group.DownloadUrlFormat, version);
                    // 智教联盟需要先获取真实下载地址
                    if (group.GroupName == "智教联盟")
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 获取智教联盟真实下载地址: {url}");
                        var realUrl = await GetZhijiaoRealDownloadUrl(url);
                        if (string.IsNullOrEmpty(realUrl))
                        {
                            LogHelper.WriteLogToFile("AutoUpdate | 智教联盟真实下载地址获取失败，跳过", LogHelper.LogType.Warning);
                            progressCallback?.Invoke(0, "智教联盟真实下载地址获取失败，跳过");
                            continue;
                        }
                        url = realUrl;
                        LogHelper.WriteLogToFile($"AutoUpdate | 智教联盟真实下载地址: {url}");
                    }
                    // inkeys线路组直接使用下载地址，无需特殊处理
                    else if (group.GroupName == "inkeys")
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 使用inkeys线路组下载地址: {url}");
                    }
                    LogHelper.WriteLogToFile($"AutoUpdate | 尝试从线路组 {group.GroupName} 下载: {url}");

                    bool downloadSuccess = await DownloadFile(url, zipFilePath, progressCallback);

                    if (downloadSuccess)
                    {
                        SaveDownloadStatus(true);
                        LogHelper.WriteLogToFile($"AutoUpdate | 从线路组 {group.GroupName} 下载成功");
                        progressCallback?.Invoke(100, "下载完成");
                        return true;
                    }

                    LogHelper.WriteLogToFile($"AutoUpdate | 线路组 {group.GroupName} 下载失败，尝试下一个线路组", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("AutoUpdate | 所有线路组下载均失败", LogHelper.LogType.Error);
                progressCallback?.Invoke(0, "所有线路组下载均失败");
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 下载更新时出错: {ex.Message}", LogHelper.LogType.Error);
                if (ex.InnerException != null)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 内部异常: {ex.InnerException.Message}", LogHelper.LogType.Error);
                }

                SaveDownloadStatus(false);
                progressCallback?.Invoke(0, $"下载异常: {ex.Message}");
                return false;
            }
        }

        // 下载文件的具体实现
        public static async Task<bool> DownloadFile(string fileUrl, string destinationPath, Action<double, string> progressCallback = null)
        {
            LogHelper.WriteLogToFile($"AutoUpdate | 正在尝试多线程下载: {fileUrl}");
            int maxRetry = 3;
            // 降低并发数，减少网络压力
            int[] threadOptions = { 32, 16, 8, 4, 1 };

            // 检查服务器是否支持Range分块下载
            bool supportRange = false;
            long totalSize = -1;
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    var req = new HttpRequestMessage(HttpMethod.Head, fileUrl);
                    req.Headers.Range = new RangeHeaderValue(0, 0);
                    var resp = await client.SendAsync(req);
                    if (resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        supportRange = true;
                        if (resp.Content.Headers.ContentRange != null && resp.Content.Headers.ContentRange.Length.HasValue)
                        {
                            totalSize = resp.Content.Headers.ContentRange.Length.Value;
                        }
                        else if (resp.Content.Headers.ContentLength.HasValue)
                        {
                            totalSize = resp.Content.Headers.ContentLength.Value;
                        }
                    }
                    else if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        supportRange = false;
                        if (resp.Content.Headers.ContentLength.HasValue)
                        {
                            totalSize = resp.Content.Headers.ContentLength.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 检查Range支持时异常: {ex.Message}", LogHelper.LogType.Warning);
            }

            if (!supportRange)
            {
                LogHelper.WriteLogToFile("AutoUpdate | 服务器不支持分块下载，自动降级为单线程下载");
                progressCallback?.Invoke(0, "服务器不支持分块下载，自动降级为单线程下载");
                return await DownloadSingleThread(fileUrl, destinationPath, totalSize, progressCallback);
            }

            foreach (int threadCount in threadOptions)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 尝试使用 {threadCount} 线程下载");
                progressCallback?.Invoke(0, $"尝试使用 {threadCount} 线程下载");

                if (totalSize <= 0)
                {
                    totalSize = await GetContentLength(fileUrl);
                }
                if (totalSize <= 0)
                {
                    progressCallback?.Invoke(0, "无法获取文件大小，取消下载");
                    return false;
                }

                // 根据文件大小动态调整分块大小，避免分块过小
                int minBlockSize = 32 * 1024; // 最小32KB
                int blockSize = Math.Max(minBlockSize, (int)Math.Ceiling((double)totalSize / threadCount));
                int blockCount = (int)Math.Ceiling((double)totalSize / blockSize);

                LogHelper.WriteLogToFile($"AutoUpdate | 文件大小: {totalSize}, 分块数: {blockCount}, 分块大小: {blockSize}");

                var blockQueue = new ConcurrentQueue<BlockTask>();
                var finishedBlocks = new ConcurrentDictionary<int, bool>();
                long[] blockDownloaded = new long[blockCount];

                for (int i = 0; i < blockCount; i++)
                {
                    long start = i * blockSize;
                    long end = Math.Min(start + blockSize - 1, totalSize - 1);
                    blockQueue.Enqueue(new BlockTask { Index = i, Start = start, End = end, RetryCount = 0 });
                }

                CancellationTokenSource cts = new CancellationTokenSource();
                var tasks = new List<Task>();

                for (int t = 0; t < threadCount; t++)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        while (blockQueue.TryDequeue(out var block))
                        {
                            bool success = false;
                            string tempPath = destinationPath + $".part{block.Index}";

                            for (int retry = block.RetryCount; retry < maxRetry && !success; retry++)
                            {
                                try
                                {
                                    using (var client = new HttpClient())
                                    {
                                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                                        var req = new HttpRequestMessage(HttpMethod.Get, fileUrl);
                                        req.Headers.Range = new RangeHeaderValue(block.Start, block.End);

                                        // 增加连接超时设置
                                        client.Timeout = TimeSpan.FromSeconds(30);

                                        var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                        var lastReadTime = DateTime.UtcNow;
                                        bool dataReceived = false;

                                        using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, downloadCts.Token))
                                        {
                                            LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index} 响应状态: {resp.StatusCode}");
                                            resp.EnsureSuccessStatusCode();
                                            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                            {
                                                var stream = await resp.Content.ReadAsStreamAsync();
                                                byte[] buffer = new byte[8192];
                                                int read;
                                                long blockDownloadedBytes = 0;

                                                while (true)
                                                {
                                                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, downloadCts.Token);
                                                    var timeoutTask = Task.Delay(20000, downloadCts.Token); // 增加到20秒超时
                                                    var completed = await Task.WhenAny(readTask, timeoutTask);
                                                    if (completed == timeoutTask)
                                                    {
                                                        LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index} 20秒无数据，线程超时重试", LogHelper.LogType.Warning);
                                                        progressCallback?.Invoke(0, $"分块{block.Index} 20秒无数据，线程超时重试");
                                                        downloadCts.Cancel();
                                                        break;
                                                    }
                                                    read = await readTask;
                                                    if (read <= 0) break;
                                                    await fs.WriteAsync(buffer, 0, read, downloadCts.Token);
                                                    blockDownloadedBytes += read;
                                                    blockDownloaded[block.Index] = blockDownloadedBytes;
                                                    lastReadTime = DateTime.UtcNow;
                                                    dataReceived = true;

                                                    // 合并所有块进度
                                                    long totalDownloaded = blockDownloaded.Sum();
                                                    double percent = (double)totalDownloaded / totalSize * 100;
                                                    progressCallback?.Invoke(percent, $"多线程下载中({threadCount}线程): {percent:F1}%");
                                                }
                                            }
                                        }

                                        if (!dataReceived)
                                        {
                                            throw new IOException("分块下载超时无数据");
                                        }

                                        // 验证分块大小是否正确
                                        var fileInfo = new FileInfo(tempPath);
                                        long expectedSize = block.End - block.Start + 1;
                                        if (fileInfo.Length != expectedSize)
                                        {
                                            LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index}大小不匹配，期望:{expectedSize}，实际:{fileInfo.Length}", LogHelper.LogType.Warning);
                                            throw new IOException($"分块{block.Index}大小不匹配");
                                        }
                                    }
                                    success = true;
                                    LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index}下载成功");
                                }
                                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                                {
                                    LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index}下载失败，第{retry + 1}次: {ex.Message}", LogHelper.LogType.Warning);
                                    progressCallback?.Invoke(0, $"分块{block.Index}下载失败，第{retry + 1}次: {ex.Message}");

                                    // 清理可能损坏的分块文件
                                    if (File.Exists(tempPath))
                                    {
                                        try { File.Delete(tempPath); } catch { }
                                    }

                                    // 增加重试间隔，避免频繁重试
                                    await Task.Delay(2000 * (retry + 1));
                                }
                            }
                            if (success)
                            {
                                finishedBlocks[block.Index] = true;
                            }
                            else if (block.RetryCount + 1 < maxRetry)
                            {
                                // 失败但未超最大重试，重新入队
                                block.RetryCount++;
                                blockQueue.Enqueue(block);
                            }
                            else
                            {
                                // 超过最大重试，取消所有任务
                                LogHelper.WriteLogToFile($"AutoUpdate | 分块{block.Index}超过最大重试次数，取消下载", LogHelper.LogType.Error);
                                cts.Cancel();
                                break;
                            }
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                if (cts.IsCancellationRequested || finishedBlocks.Count != blockCount)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | {threadCount}线程下载失败，完成分块数: {finishedBlocks.Count}/{blockCount}", LogHelper.LogType.Warning);
                    progressCallback?.Invoke(0, $"{threadCount}线程下载失败，完成分块数: {finishedBlocks.Count}/{blockCount}");

                    // 清理分块文件
                    for (int i = 0; i < blockCount; i++)
                    {
                        string tempPath = destinationPath + $".part{i}";
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }

                    if (threadCount == threadOptions.Last())
                    {
                        // 已经是最后一次尝试，降级为单线程
                        LogHelper.WriteLogToFile("AutoUpdate | 所有多线程尝试失败，降级为单线程下载");
                        progressCallback?.Invoke(0, "所有多线程尝试失败，降级为单线程下载");
                        return await DownloadSingleThread(fileUrl, destinationPath, totalSize, progressCallback);
                    }

                    LogHelper.WriteLogToFile($"AutoUpdate | {threadCount}线程下载失败，尝试降级为{threadOptions[Array.IndexOf(threadOptions, threadCount) + 1]}线程");
                    progressCallback?.Invoke(0, $"{threadCount}线程下载失败，尝试降级为{threadOptions[Array.IndexOf(threadOptions, threadCount) + 1]}线程");
                    continue;
                }

                // 合并所有块
                try
                {
                    using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        for (int i = 0; i < blockCount; i++)
                        {
                            string tempPath = destinationPath + $".part{i}";
                            if (!File.Exists(tempPath))
                            {
                                throw new FileNotFoundException($"分块文件不存在: {tempPath}");
                            }

                            using (var input = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
                            {
                                await input.CopyToAsync(output);
                            }
                            File.Delete(tempPath);
                        }
                    }

                    progressCallback?.Invoke(100, $"多线程下载完成({threadCount}线程)");
                    LogHelper.WriteLogToFile($"AutoUpdate | 多线程下载完成({threadCount}线程)");

                    // 文件大小校验
                    FileInfo fileInfo = new FileInfo(destinationPath);
                    if (fileInfo.Length != totalSize)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 文件大小校验失败，本地：{fileInfo.Length}，服务器：{totalSize}", LogHelper.LogType.Error);
                        File.Delete(destinationPath);
                        progressCallback?.Invoke(0, "文件大小校验失败，已删除损坏文件");
                        return false;
                    }

                    // ZIP文件完整性校验
                    if (destinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            ZipFile.OpenRead(destinationPath).Dispose();
                        }
                        catch
                        {
                            LogHelper.WriteLogToFile("AutoUpdate | ZIP文件解压测试失败，文件可能已损坏", LogHelper.LogType.Error);
                            File.Delete(destinationPath);
                            progressCallback?.Invoke(0, "ZIP文件解压测试失败，已删除损坏文件");
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 合并分块文件时出错: {ex.Message}", LogHelper.LogType.Error);
                    File.Delete(destinationPath);
                    progressCallback?.Invoke(0, $"合并分块文件时出错: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        // 单线程下载方法
        private static async Task<bool> DownloadSingleThread(string fileUrl, string destinationPath, long totalSize, Action<double, string> progressCallback = null)
        {
            try
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 开始单线程下载: {fileUrl}");
                progressCallback?.Invoke(0, "开始单线程下载");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    client.Timeout = TimeSpan.FromMinutes(10); // 单线程下载设置更长的超时时间

                    using (var resp = await client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var stream = await resp.Content.ReadAsStreamAsync();
                            byte[] buffer = new byte[8192];
                            int read;
                            long downloaded = 0;
                            var lastProgressUpdate = DateTime.UtcNow;

                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, read);
                                downloaded += read;

                                // 限制进度更新频率，避免UI卡顿
                                if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(500))
                                {
                                    if (totalSize > 0)
                                    {
                                        double percent = (double)downloaded / totalSize * 100;
                                        progressCallback?.Invoke(percent, $"单线程下载中: {percent:F1}%");
                                    }
                                    lastProgressUpdate = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }

                progressCallback?.Invoke(100, "单线程下载完成");
                LogHelper.WriteLogToFile("AutoUpdate | 单线程下载完成");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 单线程下载失败: {ex.Message}", LogHelper.LogType.Error);
                progressCallback?.Invoke(0, $"单线程下载失败: {ex.Message}");
                return false;
            }
        }

        // 获取文件总大小
        private static async Task<long> GetContentLength(string fileUrl)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Head, fileUrl);
                    var resp = await client.SendAsync(req);
                    if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength.HasValue)
                        return resp.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
            return -1;
        }

        // 保存下载状态
        private static void SaveDownloadStatus(bool isSuccess)
        {
            try
            {
                if (statusFilePath == null) return;

                string directory = Path.GetDirectoryName(statusFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(statusFilePath, isSuccess.ToString());
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 保存下载状态时出错: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        // 安装新版本应用
        public static void InstallNewVersionApp(string version, bool isInSilence)
        {
            try
            {
                // 在更新前备份设置文件
                try
                {
                    if (MainWindow.Settings.Advanced.IsAutoBackupBeforeUpdate)
                    {
                        string backupDir = Path.Combine(App.RootPath, "Backups");
                        if (!Directory.Exists(backupDir))
                        {
                            Directory.CreateDirectory(backupDir);
                            LogHelper.WriteLogToFile($"创建备份目录: {backupDir}");
                        }

                        string backupFileName = $"Settings_BeforeUpdate_v{version}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                        string backupPath = Path.Combine(backupDir, backupFileName);

                        string settingsJson = JsonConvert.SerializeObject(MainWindow.Settings, Formatting.Indented);
                        File.WriteAllText(backupPath, settingsJson);

                        LogHelper.WriteLogToFile($"更新前自动备份设置成功: {backupPath}");
                    }
                    else
                    {
                        LogHelper.WriteLogToFile("更新前自动备份功能已禁用，跳过备份");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"更新前自动备份设置时出错: {ex.Message}", LogHelper.LogType.Error);
                }

                string zipFilePath = Path.Combine(updatesFolderPath, $"InkCanvasForClass.CE.{version}.zip");
                LogHelper.WriteLogToFile($"AutoUpdate | 检查ZIP文件: {zipFilePath}");

                if (!File.Exists(zipFilePath))
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | ZIP文件未找到: {zipFilePath}", LogHelper.LogType.Error);
                    return;
                }

                FileInfo fileInfo = new FileInfo(zipFilePath);
                if (fileInfo.Length == 0)
                {
                    LogHelper.WriteLogToFile("AutoUpdate | ZIP文件为空，无法继续", LogHelper.LogType.Error);
                    return;
                }
                LogHelper.WriteLogToFile($"AutoUpdate | ZIP文件大小: {fileInfo.Length} 字节");

                string currentAppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                int currentProcessId = Process.GetCurrentProcess().Id;
                string appPath = Assembly.GetExecutingAssembly().Location;

                LogHelper.WriteLogToFile($"AutoUpdate | 当前应用程序目录: {currentAppDir}");
                LogHelper.WriteLogToFile($"AutoUpdate | 当前进程ID: {currentProcessId}");
                LogHelper.WriteLogToFile($"AutoUpdate | 静默更新模式: {isInSilence}");

                string batchFilePath = Path.Combine(Path.GetTempPath(), "UpdateICC_" + Guid.NewGuid().ToString().Substring(0, 8) + ".bat");
                LogHelper.WriteLogToFile($"AutoUpdate | 创建更新批处理文件: {batchFilePath}");

                StringBuilder batchContent = new StringBuilder();
                batchContent.AppendLine("@echo off");

                batchContent.AppendLine("echo Set objShell = CreateObject(\"WScript.Shell\") > \"%temp%\\hideme.vbs\"");
                batchContent.AppendLine("echo objShell.Run \"cmd /c \"\"\" ^& WScript.Arguments(0) ^& \"\"\"\", 0, True >> \"%temp%\\hideme.vbs\"");
                batchContent.AppendLine("echo Wscript.Sleep 100 >> \"%temp%\\hideme.vbs\"");

                string updateBatPath = Path.Combine(Path.GetTempPath(), "ICCUpdate_" + Guid.NewGuid().ToString().Substring(0, 8) + ".bat");
                batchContent.AppendLine($"echo @echo off > \"{updateBatPath}\"");
                batchContent.AppendLine($"echo set PROC_ID={currentProcessId} >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo :CHECK_PROCESS >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo tasklist /fi \"PID eq %PROC_ID%\" ^| find \"%PROC_ID%\" ^> nul >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if %%ERRORLEVEL%% == 0 ( >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo     timeout /t 1 /nobreak ^> nul >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo     goto CHECK_PROCESS >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo ) >> \"{updateBatPath}\"");

                batchContent.AppendLine($"echo timeout /t 1 /nobreak > nul >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo echo Application closed, starting update process... >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo timeout /t 2 /nobreak ^> nul >> \"{updateBatPath}\"");

                string extractPath = Path.Combine(updatesFolderPath, $"Extract_{version}");
                batchContent.AppendLine($"echo echo Extracting update files... >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if exist \"{extractPath}\" rd /s /q \"{extractPath}\" >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo mkdir \"{extractPath}\" >> \"{updateBatPath}\"");

                batchContent.AppendLine($"echo powershell -command \"Expand-Archive -Path '{zipFilePath.Replace("'", "''")}' -DestinationPath '{extractPath.Replace("'", "''")}' -Force\" >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if %%ERRORLEVEL%% neq 0 ( >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo     goto ERROR_EXIT >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo ) >> \"{updateBatPath}\"");

                batchContent.AppendLine($"echo echo Copying updated files to application directory... >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo xcopy /s /y /e \"{extractPath}\\*\" \"{currentAppDir}\\\" >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if %%ERRORLEVEL%% neq 0 ( >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo     goto ERROR_EXIT >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo ) >> \"{updateBatPath}\"");

                batchContent.AppendLine($"echo echo Cleaning up temporary files... >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if exist \"{extractPath}\" rd /s /q \"{extractPath}\" >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo if exist \"{zipFilePath}\" del /f /q \"{zipFilePath}\" >> \"{updateBatPath}\"");

                batchContent.AppendLine($"echo echo Update completed successfully! >> \"{updateBatPath}\"");

                if (isInSilence)
                {
                    batchContent.AppendLine($"echo echo 自动启动应用程序... >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo start \"\" \"{appPath}\" >> \"{updateBatPath}\"");
                }
                else
                {
                    batchContent.AppendLine($"echo taskkill /F /IM \"InkCanvasForClass.exe\" >nul 2>nul >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo :: 检查应用程序是否已经在运行 >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo tasklist /FI \"IMAGENAME eq InkCanvasForClass.exe\" | find /i \"InkCanvasForClass.exe\" > nul >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo if %%ERRORLEVEL%% neq 0 ( >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo     echo 启动应用程序... >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo     start \"\" \"{appPath}\" >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo ) else ( >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo     echo 应用程序已经在运行，不再重复启动 >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo ) >> \"{updateBatPath}\"");
                }

                batchContent.AppendLine($"echo exit /b 0 >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo goto EXIT >> \"{updateBatPath}\"");

                if (isInSilence)
                {
                    batchContent.AppendLine($"echo :ERROR_EXIT >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo echo Update failed! >> \"%temp%\\icc_update_error.log\" >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo exit /b 1 >> \"{updateBatPath}\"");
                }
                else
                {
                    batchContent.AppendLine($"echo :ERROR_EXIT >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo start \"\" cmd /c \"echo Update failed! ^& pause\" >> \"{updateBatPath}\"");
                    batchContent.AppendLine($"echo exit /b 1 >> \"{updateBatPath}\"");
                }

                batchContent.AppendLine($"echo :EXIT >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo del \"{updateBatPath}\" >> \"{updateBatPath}\"");
                batchContent.AppendLine($"echo exit >> \"{updateBatPath}\"");

                batchContent.AppendLine($"wscript \"%temp%\\hideme.vbs\" \"{updateBatPath}\"");
                batchContent.AppendLine("del \"%temp%\\hideme.vbs\"");
                batchContent.AppendLine("exit");

                File.WriteAllText(batchFilePath, batchContent.ToString());
                LogHelper.WriteLogToFile("AutoUpdate | 创建更新批处理文件完成");

                Process.Start(new ProcessStartInfo
                {
                    FileName = batchFilePath,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                LogHelper.WriteLogToFile("AutoUpdate | 启动更新批处理进程（隐藏窗口）");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 准备更新安装时出错: {ex.Message}", LogHelper.LogType.Error);
                if (ex.InnerException != null)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 内部异常: {ex.InnerException.Message}", LogHelper.LogType.Error);
                }
            }
        }

        // 获取远程内容的通用方法
        public static async Task<string> GetRemoteContent(string fileUrl)
        {
            // 检测是否为Windows 7
            var osVersion = Environment.OSVersion;
            bool isWindows7 = osVersion.Version.Major == 6 && osVersion.Version.Minor == 1;

            if (isWindows7)
            {
                // Windows 7使用特殊配置
                using (var handler = new HttpClientHandler())
                {
                    try
                    {
                        // 配置HttpClientHandler以支持Windows 7
                        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                        using (HttpClient client = new HttpClient(handler))
                        {
                            client.Timeout = RequestTimeout;
                            LogHelper.WriteLogToFile($"AutoUpdate | 发送HTTP请求到: {fileUrl}");
                            var downloadTask = client.GetAsync(fileUrl);
                            var timeoutTask = Task.Delay(RequestTimeout);
                            var completedTask = await Task.WhenAny(downloadTask, timeoutTask);
                            if (completedTask == timeoutTask)
                            {
                                LogHelper.WriteLogToFile($"AutoUpdate | 请求超时 ({RequestTimeout.TotalSeconds}秒)", LogHelper.LogType.Error);
                                return null;
                            }
                            HttpResponseMessage response = await downloadTask;
                            LogHelper.WriteLogToFile($"AutoUpdate | HTTP响应状态: {response.StatusCode}");
                            response.EnsureSuccessStatusCode();
                            string content = await response.Content.ReadAsStringAsync();
                            return content;
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | HTTP请求错误: {ex.Message}", LogHelper.LogType.Error);
                    }
                    catch (TaskCanceledException ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 请求超时: {ex.Message}", LogHelper.LogType.Error);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 错误: {ex.Message}", LogHelper.LogType.Error);
                    }
                    return null;
                }
            }

            // 其他Windows版本使用标准配置
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    client.Timeout = RequestTimeout;
                    LogHelper.WriteLogToFile($"AutoUpdate | 发送HTTP请求到: {fileUrl}");
                    var downloadTask = client.GetAsync(fileUrl);
                    var timeoutTask = Task.Delay(RequestTimeout);
                    var completedTask = await Task.WhenAny(downloadTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        LogHelper.WriteLogToFile($"AutoUpdate | 请求超时 ({RequestTimeout.TotalSeconds}秒)", LogHelper.LogType.Error);
                        return null;
                    }
                    HttpResponseMessage response = await downloadTask;
                    LogHelper.WriteLogToFile($"AutoUpdate | HTTP响应状态: {response.StatusCode}");
                    response.EnsureSuccessStatusCode();
                    string content = await response.Content.ReadAsStringAsync();
                    return content;
                }
                catch (HttpRequestException ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | HTTP请求错误: {ex.Message}", LogHelper.LogType.Error);
                }
                catch (TaskCanceledException ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 请求超时: {ex.Message}", LogHelper.LogType.Error);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 错误: {ex.Message}", LogHelper.LogType.Error);
                }
                return null;
            }
        }

        // 使用指定线路组获取更新日志
        public static async Task<string> GetUpdateLogWithLineGroup(UpdateLineGroup group)
        {
            return await GetRemoteContent(group.LogUrl);
        }

        // 获取更新日志（自动选择最快线路组）
        public static async Task<string> GetUpdateLog(UpdateChannel channel = UpdateChannel.Release)
        {
            var group = await GetFastestLineGroup(channel);
            if (group == null) return "# 无法获取更新日志\n\n所有线路均不可用。";
            return await GetUpdateLogWithLineGroup(group);
        }

        // 删除更新文件夹
        public static void DeleteUpdatesFolder()
        {
            try
            {
                if (Directory.Exists(updatesFolderPath))
                {
                    foreach (string file in Directory.GetFiles(updatesFolderPath, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { }
                    }
                    foreach (string dir in Directory.GetDirectories(updatesFolderPath))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                    try { Directory.Delete(updatesFolderPath, true); } catch { }
                }
            }
            catch { }
        }

        // 版本修复方法，强制下载并安装指定通道的最新版本
        public static async Task<bool> FixVersion(UpdateChannel channel = UpdateChannel.Release)
        {
            try
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 开始修复版本，通道: {channel}");

                // 获取远程版本号（自动选择最快线路组，始终下载远程版本，版本修复模式）
                var (remoteVersion, group, _) = await CheckForUpdates(channel, true, true);
                if (string.IsNullOrEmpty(remoteVersion) || group == null)
                {
                    LogHelper.WriteLogToFile("AutoUpdate | 修复版本时获取远程版本失败", LogHelper.LogType.Error);
                    return false;
                }

                LogHelper.WriteLogToFile($"AutoUpdate | 修复版本远程版本: {remoteVersion}");

                // 无论版本是否为最新，都下载远程版本
                bool downloadResult = await DownloadSetupFile(remoteVersion, group);
                if (!downloadResult)
                {
                    LogHelper.WriteLogToFile("AutoUpdate | 修复版本时下载更新失败", LogHelper.LogType.Error);
                    return false;
                }

                // 执行安装，非静默模式
                InstallNewVersionApp(remoteVersion, false);
                App.IsAppExitByUser = true;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | FixVersion错误: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        // 获取所有GitHub历史版本（Release）
        public static async Task<List<(string version, string downloadUrl, string releaseNotes)>> GetAllGithubReleases(UpdateChannel channel = UpdateChannel.Release)
        {
            var result = new List<(string, string, string)>();
            try
            {
                string apiUrl = channel == UpdateChannel.Beta
                    ? "https://api.github.com/repos/InkCanvasForClass/community-beta/releases"
                    : "https://api.github.com/repos/InkCanvasForClass/community/releases";
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "ICC-CE Auto Updater");
                    var response = await client.GetStringAsync(apiUrl);
                    var arr = JArray.Parse(response);
                    foreach (var item in arr)
                    {
                        string version = item["tag_name"]?.ToString();
                        string releaseNotes = item["body"]?.ToString();
                        string downloadUrl = item["assets"]?.First?["browser_download_url"]?.ToString();
                        if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(downloadUrl))
                            result.Add((version, downloadUrl, releaseNotes));
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 获取历史版本失败: {ex.Message}", LogHelper.LogType.Error);
            }
            return result;
        }

        // 测试Windows 7 TLS连接的方法
        public static async Task<bool> TestWindows7TlsConnection()
        {
            try
            {
                // 检测是否为Windows 7
                var osVersion = Environment.OSVersion;
                bool isWindows7 = osVersion.Version.Major == 6 && osVersion.Version.Minor == 1;

                if (!isWindows7)
                {
                    LogHelper.WriteLogToFile("AutoUpdate | 当前系统不是Windows 7，跳过TLS连接测试");
                    return true; // 非Windows 7系统直接返回成功
                }

                LogHelper.WriteLogToFile("AutoUpdate | 开始测试Windows 7 TLS连接...");

                // 测试GitHub连接
                var testUrl = "https://github.com/InkCanvasForClass/community/raw/refs/heads/main/AutomaticUpdateVersionControl.txt";

                using (var handler = new HttpClientHandler())
                {
                    handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(10);
                        var response = await client.GetAsync(testUrl);

                        if (response.IsSuccessStatusCode)
                        {
                            LogHelper.WriteLogToFile("AutoUpdate | Windows 7 TLS连接测试成功");
                            return true;
                        }

                        LogHelper.WriteLogToFile($"AutoUpdate | Windows 7 TLS连接测试失败，状态码: {response.StatusCode}", LogHelper.LogType.Error);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | Windows 7 TLS连接测试异常: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 获取指定版本的发布时间
        /// </summary>
        /// <param name="version">版本号</param>
        /// <param name="channel">更新通道</param>
        /// <returns>版本发布时间，如果获取失败则返回null</returns>
        public static async Task<DateTime?> GetVersionReleaseTime(string version, UpdateChannel channel = UpdateChannel.Release)
        {
            try
            {
                var (_, _, _, releaseTime) = await GetGithubReleaseByVersion(version, channel);
                return releaseTime;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 获取版本 {version} 发布时间失败: {ex.Message}", LogHelper.LogType.Warning);
                return null;
            }
        }

        /// <summary>
        /// 启动手动指定版本的多线路多线程下载并自动安装（用于历史版本回滚等场景）
        /// </summary>
        public static async Task<bool> StartManualDownloadAndInstall(string version, UpdateChannel channel, Action<double, string> progressCallback = null)
        {
            try
            {
                // 先检测并排序所有可用线路组
                var groups = await GetAvailableLineGroupsOrdered(channel);
                bool downloadSuccess = await DownloadSetupFileWithFallback(version, groups, progressCallback);
                if (!downloadSuccess)
                {
                    LogHelper.WriteLogToFile($"AutoUpdate | 手动下载版本{version}失败");
                    return false;
                }
                LogHelper.WriteLogToFile($"AutoUpdate | 手动安装版本: {version}");
                InstallNewVersionApp(version, false);
                App.IsAppExitByUser = true;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Application.Current.Shutdown();
                });
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"AutoUpdate | 手动下载或安装异常: {ex.Message}", LogHelper.LogType.Error);
                progressCallback?.Invoke(0, $"下载异常: {ex.Message}");
                return false;
            }
        }
    }

    internal class AutoUpdateWithSilenceTimeComboBox
    {
        public static ObservableCollection<string> Hours { get; set; } = new ObservableCollection<string>();
        public static ObservableCollection<string> Minutes { get; set; } = new ObservableCollection<string>();

        public static void InitializeAutoUpdateWithSilenceTimeComboBoxOptions(ComboBox startTimeComboBox, ComboBox endTimeComboBox)
        {
            for (int hour = 0; hour <= 23; ++hour)
            {
                Hours.Add(hour.ToString("00"));
            }
            for (int minute = 0; minute <= 59; minute += 20)
            {
                Minutes.Add(minute.ToString("00"));
            }
            startTimeComboBox.ItemsSource = Hours.SelectMany(h => Minutes.Select(m => $"{h}:{m}"));
            endTimeComboBox.ItemsSource = Hours.SelectMany(h => Minutes.Select(m => $"{h}:{m}"));
        }

        public static bool CheckIsInSilencePeriod(string startTime, string endTime)
        {
            if (startTime == endTime) return true;
            DateTime currentTime = DateTime.Now;

            DateTime StartTime = DateTime.ParseExact(startTime, "HH:mm", null);
            DateTime EndTime = DateTime.ParseExact(endTime, "HH:mm", null);
            if (StartTime <= EndTime)
            { // 单日时间段
                return currentTime >= StartTime && currentTime <= EndTime;
            }  // 跨越两天的时间段
            return currentTime >= StartTime || currentTime <= EndTime;
        }
    }
}
