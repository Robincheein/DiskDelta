using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DiskDelta
{
    internal sealed class UpdateRelease
    {
        public Version Version;
        public long AssetId;
        public long Bytes;
        public string Hash;
    }

    internal sealed class UpdateCheck
    {
        public UpdateRelease Release;
        public string Message;
    }

    internal sealed class GitHubUpdates
    {
        public const string ReleasePage = "https://github.com/Robincheein/DiskDelta/releases/latest";
        private const string RepoApi = "https://api.github.com/repos/Robincheein/DiskDelta/releases/";
        private const string GitRemote = "https://github.com/Robincheein/DiskDelta.git";
        private const int MaxAssetBytes = 32 * 1024 * 1024;
        private readonly string dataPath;
        private readonly string executable;
        private readonly object sync = new object();
        private Task<UpdateCheck> lastRequest;

        [DataContract]
        private sealed class ReleaseInfo
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public AssetInfo[] Assets { get; set; }
        }

        [DataContract]
        private sealed class AssetInfo
        {
            [DataMember(Name = "id")] public long Id { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
            [DataMember(Name = "state")] public string State { get; set; }
        }

        public GitHubUpdates(string dataPath, string executable)
        {
            this.dataPath = dataPath;
            this.executable = executable;
        }

        private string ProxyFile { get { return Path.Combine(dataPath, "update-proxy.txt"); } }
        public Version CurrentVersion { get { return VersionOf(executable); } }

        public Task<UpdateCheck> CheckAsync(bool refresh)
        {
            lock (sync)
            {
                if (lastRequest == null || (refresh && lastRequest.IsCompleted))
                    lastRequest = Task.Run((Func<UpdateCheck>)Check);
                return lastRequest;
            }
        }

        public string ReadProxy()
        {
            return File.Exists(ProxyFile) ? File.ReadAllText(ProxyFile).Trim() : "";
        }

        public void WriteProxy(string value)
        {
            value = value.Trim();
            if (value.Length == 0)
            {
                if (File.Exists(ProxyFile)) File.Delete(ProxyFile);
                return;
            }
            ValidateProxy(value);
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(ProxyFile, value);
        }

        private UpdateCheck Check()
        {
            try
            {
                UpdateInstaller.CleanCompleted(executable);
                string git = GitPath();
                string token = GitToken(git);
                IWebProxy proxy = Proxy(git);
                using (HttpWebResponse response = Request(new Uri(RepoApi + "latest"), "application/vnd.github+json", token, proxy))
                using (Stream stream = response.GetResponseStream())
                {
                    var release = (ReleaseInfo)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(stream);
                    Version version;
                    if (release == null || release.Draft || release.Prerelease ||
                        !Version.TryParse((release.Tag ?? "").TrimStart('v'), out version))
                        throw new IOException("无法解析 GitHub 发布版本。");
                    version = Normalize(version);
                    if (version <= CurrentVersion) return new UpdateCheck { Message = "已是最新版" };

                    AssetInfo asset = (release.Assets ?? new AssetInfo[0]).FirstOrDefault(
                        item => item.Name == "DiskDelta.exe" && item.State == "uploaded");
                    string hash = asset != null && asset.Digest != null && asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ?
                        asset.Digest.Substring(7) : null;
                    if (asset == null || asset.Id <= 0 || asset.Size <= 0 || asset.Size > MaxAssetBytes || !IsHash(hash))
                        throw new IOException("此版本缺少可校验的 DiskDelta.exe。");
                    return new UpdateCheck
                    {
                        Message = "有新版本 " + DisplayVersion(version),
                        Release = new UpdateRelease { Version = version, AssetId = asset.Id, Bytes = asset.Size, Hash = hash }
                    };
                }
            }
            catch (WebException ex)
            {
                using (var response = ex.Response as HttpWebResponse)
                {
                    if (response != null && (response.StatusCode == HttpStatusCode.Unauthorized ||
                        response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Forbidden))
                        return new UpdateCheck { Message = "请用有仓库权限的 GitHub 账号登录 Git Credential Manager。" };
                }
                return new UpdateCheck { Message = "无法连接 GitHub" };
            }
            catch (Exception ex) { return new UpdateCheck { Message = "检查失败 · " + ex.Message }; }
        }

        public void Download(UpdateRelease release, string target)
        {
            string git = GitPath();
            string token = GitToken(git);
            IWebProxy proxy = Proxy(git);
            var address = new Uri(RepoApi + "assets/" + release.AssetId.ToString(CultureInfo.InvariantCulture));
            for (int redirects = 0; redirects < 4; redirects++)
            {
                using (HttpWebResponse response = Request(address, "application/octet-stream", token, proxy))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        Uri next;
                        if (!Uri.TryCreate(address, response.Headers[HttpResponseHeader.Location] ?? "", out next) || !IsAssetHost(next))
                            throw new IOException("GitHub 返回了无法识别的下载地址。");
                        address = next;
                        continue;
                    }
                    using (Stream stream = response.GetResponseStream())
                    using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[64 * 1024];
                        long bytes = 0;
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            bytes += read;
                            if (bytes > release.Bytes) throw new InvalidDataException("下载大小与 GitHub 发布记录不一致。");
                            file.Write(buffer, 0, read);
                        }
                        if (bytes != release.Bytes) throw new InvalidDataException("下载未完成。");
                    }
                    Verify(target, release.Hash);
                    if (VersionOf(target) != release.Version) throw new InvalidDataException("下载的 EXE 版本与 GitHub 标签不一致。");
                    return;
                }
            }
            throw new IOException("GitHub 下载重定向次数过多。");
        }

        private IWebProxy Proxy(string git)
        {
            string value = ReadProxy();
            if (value.Length == 0) value = RunGit(git, "config --get-urlmatch http.proxy " + GitRemote, null).Trim();
            return value.Length == 0 ? WebRequest.GetSystemWebProxy() : new WebProxy(ValidateProxy(value));
        }

        private static Uri ValidateProxy(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("代理格式为 http://主机:端口。");
            return uri;
        }

        private static HttpWebResponse Request(Uri address, string accept, string token, IWebProxy proxy)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 10000;
            request.Proxy = proxy;
            request.UserAgent = "DiskDelta";
            request.Accept = accept;
            if (address.Host == "api.github.com")
            {
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                if (!string.IsNullOrEmpty(token)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            }
            return (HttpWebResponse)request.GetResponse();
        }

        private static bool IsAssetHost(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com" ||
                 uri.Host == "github-releases.githubusercontent.com" || uri.Host == "github.com");
        }

        private static string GitToken(string git)
        {
            string result = RunGit(git, "credential-manager get", "protocol=https\nhost=github.com\n\n");
            foreach (string line in result.Split('\n'))
                if (line.StartsWith("password=", StringComparison.Ordinal)) return line.Substring(9).TrimEnd('\r');
            return null;
        }

        private static string GitPath()
        {
            foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length < 3 || directory[1] != ':' || directory[2] != '\\') continue;
                try
                {
                    string file = Path.Combine(directory, "git.exe");
                    if (File.Exists(file)) return file;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private static string RunGit(string git, string args, string input)
        {
            if (git == null) return "";
            var start = new ProcessStartInfo(git, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            start.EnvironmentVariables["GCM_GUI_PROMPT"] = "0";
            try
            {
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    using (var writer = process.StandardInput)
                        if (input != null) writer.Write(input);
                    if (!process.WaitForExit(5500))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        return "";
                    }
                    error.GetAwaiter().GetResult();
                    return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : "";
                }
            }
            catch (System.ComponentModel.Win32Exception) { return ""; }
            catch (IOException) { return ""; }
        }

        internal static Version VersionOf(string path)
        {
            var file = FileVersionInfo.GetVersionInfo(path);
            if (file.ProductName != "DiskDelta") throw new InvalidDataException("文件不是磁盘日记。");
            return new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        public static string DisplayVersion(Version version)
        {
            return "v" + version.ToString(version.Revision > 0 ? 4 : 3);
        }

        internal static bool IsHash(string hash)
        {
            return hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
        }

        internal static void Verify(string file, string expected)
        {
            using (SHA256 sha = SHA256.Create())
            using (Stream stream = File.OpenRead(file))
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 校验失败。");
            }
        }
    }
}
