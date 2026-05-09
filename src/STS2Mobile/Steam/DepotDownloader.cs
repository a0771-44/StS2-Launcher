using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace STS2Mobile.Steam;

public class DownloadProgress
{
    public long TotalBytes;
    public long DownloadedBytes;
    public int TotalFiles;
    public int CompletedFiles;
    public string CurrentFile;

    public double Percentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100.0 : 0;
}

// Downloads game files from Steam CDN using SteamKit2. Supports delta updates
// by comparing manifests, concurrent chunk downloads, and server rotation with
// retry logic. Also patches the PCK to remove the Sentry plugin (no ARM64 build).
public class DepotDownloader : IDisposable
{
    private const uint AppId = 2868840;
    private const int MaxRetries = 5;
    private const int MaxConcurrentDownloads = 8;

    private readonly SteamConnection _connection;
    private readonly string _gameDir;
    private readonly string _baseStateDir; // 브랜치별 격리를 위해 기본 경로 저장
    private string _stateDir;              // 현재 선택된 브랜치의 상태 경로
    private readonly Client _cdnClient;
    private readonly DownloadProgress _progress = new();

    // 브랜치 설정 필드 (기본값 public)
    private string _selectedBranch = "public";

    private IReadOnlyList<Server> _servers;
    private int _serverIndex;
    private readonly Dictionary<(uint, string), string> _cdnAuthTokens = new();
    private readonly Dictionary<uint, (ulong Code, DateTime Expiry)> _manifestRequestCodes = new();
    private readonly Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> _appInfoCache = new();

    public event Action<DownloadProgress> ProgressChanged;
    public event Action<string> LogMessage;

    public DepotDownloader(SteamConnection connection, string dataDir)
    {
        _connection = connection;
        _gameDir = Path.Combine(dataDir, "game");
        _baseStateDir = Path.Combine(dataDir, "download_state");
        _cdnClient = new Client(connection.Client);
        
        // 초기 상태 디렉토리 설정
        UpdateStateDirectory();
    }

    // 브랜치를 변경하는 메서드 (다운로드 시작 전 호출)
    public void SetBranch(string branchName)
    {
        _selectedBranch = string.IsNullOrEmpty(branchName) ? "public" : branchName;
        UpdateStateDirectory();
        Log($"Target branch set to: {_selectedBranch}");
    }

    private void UpdateStateDirectory()
    {
        _stateDir = Path.Combine(_baseStateDir, _selectedBranch);
        Directory.CreateDirectory(_stateDir);
    }

    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            var appInfo = await GetAppInfoAsync(AppId);
            if (appInfo == null) throw new Exception("Failed to get app info");

            var depots = await ParseDepotsAsync(appInfo.KeyValues["depots"]);

            foreach (var (depotId, manifestId) in depots)
            {
                ct.ThrowIfCancellationRequested();
                if (LoadCachedManifestId(depotId) != manifestId)
                {
                    Log($"Update available: depot {depotId} branch {_selectedBranch} manifest changed");
                    return true;
                }
            }
            Log($"Game on branch {_selectedBranch} is up to date");
            return false;
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        _connection.SuspendIdleTimeout();
        try
        {
            Directory.CreateDirectory(_gameDir);

            Log($"Fetching app info for branch: {_selectedBranch}...");
            var appInfo = await GetAppInfoAsync(AppId);
            if (appInfo == null) throw new Exception("Failed to get app info");

            var depotSection = appInfo.KeyValues["depots"];
            var depots = await ParseDepotsAsync(depotSection);
            
            if (depots.Count == 0)
                throw new Exception($"No downloadable depots found for branch {_selectedBranch}");

            // CDN 서버 로드 로직 (기존과 동일)
            Log("Getting CDN servers...");
            var allServers = await ContentServerDirectoryService.LoadAsync(_connection.Configuration, ct);
            _servers = allServers.Where(s => s.Type == "SteamCache" || s.Type == "CDN")
                                 .OrderBy(s => s.WeightedLoad).ToList();

            foreach (var (depotId, manifestId) in depots)
            {
                ct.ThrowIfCancellationRequested();
                await DownloadDepotAsync(depotId, manifestId, ct);
            }

            Log($"All game files for branch {_selectedBranch} downloaded!");
            PatchGamePck(Path.Combine(_gameDir, "SlayTheSpire2.pck"));
        }
        finally
        {
            _connection.ResumeIdleTimeout();
        }
    }

    private async Task<List<(uint DepotId, ulong ManifestId)>> ParseDepotsAsync(KeyValue depotSection)
    {
        var result = new List<(uint, ulong)>();

        foreach (var depot in depotSection.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId)) continue;

            // OS 체크 (기존 유지)
            var config = depot["config"];
            if (config != KeyValue.Invalid)
            {
                var oslist = config["oslist"]?.Value;
                if (oslist != null && !oslist.Contains("windows")) continue;
            }

            var manifests = depot["manifests"];
            if (manifests == KeyValue.Invalid) continue;

            // 핵심 수정: 선택된 브랜치의 Manifest를 먼저 찾고, 없으면 public으로 Fallback
            var branchManifest = manifests[_selectedBranch];
            if (branchManifest == KeyValue.Invalid && _selectedBranch != "public")
            {
                Log($"Depot {depotId} has no manifest for {_selectedBranch}, falling back to public");
                branchManifest = manifests["public"];
            }

            var gidNode = branchManifest["gid"];
            if (gidNode == KeyValue.Invalid || string.IsNullOrEmpty(gidNode.Value)) continue;

            if (ulong.TryParse(gidNode.Value, out var manifestId))
            {
                result.Add((depotId, manifestId));
            }
        }
        return result;
    }

    private async Task<ulong> GetManifestRequestCodeAsync(uint depotId, ulong manifestId)
    {
        if (_manifestRequestCodes.TryGetValue(depotId, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.Code;

        // 핵심 수정: "public" 하드코딩을 _selectedBranch로 변경
        var code = await _connection.Content.GetManifestRequestCode(depotId, AppId, manifestId, _selectedBranch);
        
        if (code == 0)
            throw new Exception($"Failed to get manifest code for {depotId} on branch {_selectedBranch}.");

        _manifestRequestCodes[depotId] = (code, DateTime.UtcNow.AddMinutes(5));
        return code;
    }

    private async Task DownloadDepotAsync(uint depotId, ulong manifestId, CancellationToken ct)
    {
        Log($"Processing depot {depotId}...");

        bool isUpdate = LoadCachedManifestId(depotId) != manifestId;

        var keyResult = await _connection.Apps.GetDepotDecryptionKey(depotId, AppId);
        if (keyResult.Result != EResult.OK)
            throw new Exception($"Failed to get depot key for {depotId}: {keyResult.Result}");
        var depotKey = keyResult.DepotKey;

        var manifestRequestCode = await GetManifestRequestCodeAsync(depotId, manifestId);

        Log($"Downloading manifest for depot {depotId}...");
        DepotManifest manifest = null;
        for (int attempt = 0; attempt < MaxRetries && manifest == null; attempt++)
        {
            var server = GetNextServer();
            try
            {
                manifest = await _cdnClient.DownloadManifestAsync(
                    depotId,
                    manifestId,
                    manifestRequestCode,
                    server,
                    depotKey
                );
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                var token = await GetCdnAuthToken(depotId, server);
                if (token != null)
                {
                    manifest = await _cdnClient.DownloadManifestAsync(
                        depotId,
                        manifestId,
                        manifestRequestCode,
                        server,
                        depotKey,
                        cdnAuthToken: token
                    );
                }
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                Log($"Manifest download failed (attempt {attempt + 1}): {ex.Message}");
            }
        }

        if (manifest == null)
            throw new Exception(
                $"Failed to download manifest for depot {depotId} after {MaxRetries} attempts"
            );

        var oldManifest = LoadCachedManifest(depotId);

        // Clean up temp files from interrupted previous downloads.
        foreach (
            var temp in Directory.GetFiles(_gameDir, "*.downloading", SearchOption.AllDirectories)
        )
        {
            try
            {
                File.Delete(temp);
            }
            catch { }
        }

        // Determine which files need downloading: new/changed files from the
        // manifest diff, plus any existing files that fail on-disk SHA-1 verification.
        var filesToDownload = GetFilesNeedingDownload(oldManifest, manifest, isUpdate);
        var filesToDelete = GetFilesToDelete(oldManifest, manifest);

        foreach (var fileName in filesToDelete)
        {
            var path = Path.Combine(_gameDir, fileName.Replace('\\', '/'));
            if (File.Exists(path))
            {
                File.Delete(path);
                Log($"Deleted: {fileName}");
            }
        }

        _progress.TotalFiles = filesToDownload.Count;
        _progress.CompletedFiles = 0;
        _progress.TotalBytes = filesToDownload.Sum(f => (long)f.TotalSize);
        _progress.DownloadedBytes = 0;
        ReportProgress();

        if (filesToDownload.Count == 0)
        {
            Log($"Depot {depotId}: already up to date");
        }
        else
        {
            Log(
                $"Downloading {filesToDownload.Count} files ({FormatSize(_progress.TotalBytes)}) with {MaxConcurrentDownloads} threads..."
            );

            using var semaphore = new SemaphoreSlim(MaxConcurrentDownloads);
            var tasks = new List<Task>();

            foreach (var file in filesToDownload)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            try
                            {
                                await DownloadFileAsync(file, depotId, depotKey, ct);
                                Interlocked.Increment(ref _progress.CompletedFiles);
                                ReportProgress();
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        },
                        ct
                    )
                );
            }

            await Task.WhenAll(tasks);
        }

        SaveManifest(depotId, manifest, manifestId);
        Log($"Depot {depotId} complete");
    }

    private async Task DownloadFileAsync(
        DepotManifest.FileData file,
        uint depotId,
        byte[] depotKey,
        CancellationToken ct
    )
    {
        var fileName = file.FileName.Replace('\\', '/');
        _progress.CurrentFile = fileName;
        ReportProgress();

        if (file.Flags.HasFlag(EDepotFileFlag.Directory))
        {
            Directory.CreateDirectory(Path.Combine(_gameDir, fileName));
            return;
        }

        var filePath = Path.Combine(_gameDir, fileName);
        var fileDir = Path.GetDirectoryName(filePath);
        if (fileDir != null)
            Directory.CreateDirectory(fileDir);

        // Validate existing file against manifest SHA-1 hash. A size-only check
        // would miss corruption from interrupted writes (SetLength pre-allocates).
        if (File.Exists(filePath) && VerifyFileHash(filePath, file))
        {
            Interlocked.Add(ref _progress.DownloadedBytes, (long)file.TotalSize);
            ReportProgress();
            return;
        }

        // Write to a temp file, verify hash, then move into place. This prevents
        // a partially-written file from being mistaken as complete on retry.
        var tempPath = filePath + ".downloading";

        using (var fs = File.Create(tempPath))
        {
            foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
            {
                ct.ThrowIfCancellationRequested();

                var buffer = new byte[chunk.UncompressedLength];
                int written = 0;

                for (int attempt = 0; attempt < MaxRetries; attempt++)
                {
                    var server = GetNextServer();
                    try
                    {
                        written = await _cdnClient.DownloadDepotChunkAsync(
                            depotId,
                            chunk,
                            server,
                            buffer,
                            depotKey
                        );

                        if (!VerifyChunkHash(buffer, written, chunk))
                        {
                            if (attempt < MaxRetries - 1)
                            {
                                Log($"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying...");
                                written = 0;
                                continue;
                            }
                            throw new Exception(
                                $"Chunk SHA-1 verification failed for {fileName} "
                                    + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                            );
                        }

                        break;
                    }
                    catch (SteamKitWebRequestException ex)
                        when (ex.StatusCode == HttpStatusCode.Forbidden)
                    {
                        var token = await GetCdnAuthToken(depotId, server);
                        if (token != null)
                        {
                            written = await _cdnClient.DownloadDepotChunkAsync(
                                depotId,
                                chunk,
                                server,
                                buffer,
                                depotKey,
                                cdnAuthToken: token
                            );

                            if (!VerifyChunkHash(buffer, written, chunk))
                            {
                                if (attempt < MaxRetries - 1)
                                {
                                    Log(
                                        $"Chunk SHA-1 mismatch at offset {chunk.Offset}, retrying..."
                                    );
                                    written = 0;
                                    continue;
                                }
                                throw new Exception(
                                    $"Chunk SHA-1 verification failed for {fileName} "
                                        + $"at offset {chunk.Offset} after {MaxRetries} attempts"
                                );
                            }

                            break;
                        }
                    }
                    catch (Exception ex) when (attempt < MaxRetries - 1)
                    {
                        Log($"Chunk download failed (attempt {attempt + 1}): {ex.Message}");
                    }
                }

                if (written == 0 && chunk.UncompressedLength > 0)
                    throw new Exception(
                        $"Failed to download chunk for {fileName} after {MaxRetries} attempts"
                    );

                fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                fs.Write(buffer, 0, written);

                Interlocked.Add(ref _progress.DownloadedBytes, written);
                ReportProgress();
            }
        }

        // Verify the completed file before committing it.
        if (!VerifyFileHash(tempPath, file))
        {
            File.Delete(tempPath);
            throw new Exception($"SHA-1 verification failed for {fileName} after download");
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    // Computes SHA-1 of a decompressed chunk and compares it to the manifest ChunkID.
    private static bool VerifyChunkHash(byte[] buffer, int length, DepotManifest.ChunkData chunk)
    {
        if (chunk.ChunkID == null || chunk.ChunkID.Length == 0)
            return true;

        var hash = System.Security.Cryptography.SHA1.HashData(buffer.AsSpan(0, length));
        return hash.AsSpan().SequenceEqual(chunk.ChunkID);
    }

    // Computes SHA-1 of a file on disk and compares it to the manifest hash.
    private static bool VerifyFileHash(string path, DepotManifest.FileData file)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != (long)file.TotalSize)
                return false;

            using var fs = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA1.HashData(fs);
            return hash.AsSpan().SequenceEqual(file.FileHash);
        }
        catch
        {
            return false;
        }
    }

    // Builds the list of files that need downloading. For manifest changes, uses
    // the hash diff. For all files in the target manifest, verifies the on-disk
    // copy against the expected SHA-1 — catching corruption from interrupted
    // writes, disk errors, or missing files.
    private List<DepotManifest.FileData> GetFilesNeedingDownload(
        DepotManifest oldManifest,
        DepotManifest newManifest,
        bool isUpdate
    )
    {
        var oldFiles = oldManifest?.Files.ToDictionary(f => f.FileName);
        var result = new List<DepotManifest.FileData>();
        int verified = 0;
        int corrupt = 0;

        foreach (var file in newManifest.Files)
        {
            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                continue;

            // Manifest changed for this file — always re-download.
            if (isUpdate && oldFiles != null)
            {
                if (
                    !oldFiles.TryGetValue(file.FileName, out var oldFile)
                    || !file.FileHash.SequenceEqual(oldFile.FileHash)
                )
                {
                    result.Add(file);
                    continue;
                }
            }

            // Verify on-disk file matches the manifest hash.
            var filePath = Path.Combine(_gameDir, file.FileName.Replace('\\', '/'));
            if (VerifyFileHash(filePath, file))
            {
                verified++;
            }
            else
            {
                if (File.Exists(filePath))
                {
                    corrupt++;
                    Log($"File needs re-download (hash mismatch): {file.FileName}");
                }
                result.Add(file);
            }
        }

        if (verified > 0)
            Log($"Verified {verified} existing files");
        if (corrupt > 0)
            Log($"Found {corrupt} corrupt files requiring re-download");

        return result;
    }

    private static List<string> GetFilesToDelete(
        DepotManifest oldManifest,
        DepotManifest newManifest
    )
    {
        if (oldManifest == null)
            return new List<string>();

        var newFiles = new HashSet<string>(newManifest.Files.Select(f => f.FileName));
        return oldManifest
            .Files.Where(f => !newFiles.Contains(f.FileName))
            .Select(f => f.FileName)
            .ToList();
    }

    private ulong LoadCachedManifestId(uint depotId)
    {
        var path = Path.Combine(_stateDir, $"{depotId}.id");
        if (!File.Exists(path))
            return 0;

        return ulong.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : 0;
    }

    private DepotManifest LoadCachedManifest(uint depotId)
    {
        var path = Path.Combine(_stateDir, $"{depotId}.manifest");
        if (!File.Exists(path))
            return null;

        try
        {
            using var fs = File.OpenRead(path);
            return DepotManifest.Deserialize(fs);
        }
        catch
        {
            return null;
        }
    }

    private void SaveManifest(uint depotId, DepotManifest manifest, ulong manifestId)
    {
        using (var fs = File.Create(Path.Combine(_stateDir, $"{depotId}.manifest")))
        {
            manifest.Serialize(fs);
        }
        File.WriteAllText(Path.Combine(_stateDir, $"{depotId}.id"), manifestId.ToString());
    }

    private void Log(string msg)
    {
        PatchHelper.Log($"[Depot] {msg}");
        LogMessage?.Invoke(msg);
    }

    private void ReportProgress()
    {
        ProgressChanged?.Invoke(_progress);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    // Patches the PCK in-place to disable the Sentry autoload and GDExtension
    // entries (no android.arm64 build exists for the Sentry plugin).
    public static void PatchGamePck(string pckPath)
    {
        if (!File.Exists(pckPath))
            return;

        try
        {
            using var fs = new FileStream(pckPath, FileMode.Open, FileAccess.ReadWrite);
            using var reader = new BinaryReader(fs);

            uint magic = reader.ReadUInt32();
            if (magic != 0x43504447) // "GDPC"
                return;

            uint formatVersion = reader.ReadUInt32();
            reader.ReadUInt32(); // major
            reader.ReadUInt32(); // minor
            reader.ReadUInt32(); // patch
            uint flags = reader.ReadUInt32();
            long fileBase = reader.ReadInt64();
            long dirBase = reader.ReadInt64();
            fs.Seek(16 * 4, SeekOrigin.Current); // 16 reserved uint32s

            bool relativeOffsets = (flags & 0x02) != 0;

            fs.Position = dirBase;
            uint fileCount = reader.ReadUInt32();
            bool patched = false;

            for (uint i = 0; i < fileCount; i++)
            {
                uint pathLen = reader.ReadUInt32();
                byte[] pathBytes = reader.ReadBytes((int)pathLen);
                string path = System.Text.Encoding.UTF8.GetString(pathBytes).TrimEnd('\0');
                long offset = reader.ReadInt64();
                long size = reader.ReadInt64();
                reader.ReadBytes(16); // MD5
                reader.ReadUInt32(); // flags

                long absOffset = relativeOffsets ? fileBase + offset : offset;

                if (path == "res://project.godot")
                    patched |= PatchProjectGodot(fs, absOffset, size);
                else if (path == "res://.godot/extension_list.cfg")
                    patched |= PatchExtensionList(fs, absOffset, size);
            }

            if (patched)
                PatchHelper.Log("Patched game PCK: removed Sentry plugin references");
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"PCK patching failed (non-fatal): {ex.Message}");
        }
    }

    private static bool PatchProjectGodot(FileStream fs, long offset, long size)
    {
        long savedPos = fs.Position;
        fs.Position = offset;
        var content = new byte[size];
        fs.ReadExactly(content, 0, (int)size);

        // Comment out the Sentry autoload line by replacing 'S' with ';'.
        var search = System.Text.Encoding.UTF8.GetBytes(
            "SentryInit=\"*res://addons/sentry/SentryInit.gd\""
        );
        int idx = FindBytes(content, search);
        if (idx < 0)
        {
            fs.Position = savedPos;
            return false;
        }

        content[idx] = (byte)';';
        fs.Position = offset;
        fs.Write(content, 0, content.Length);
        fs.Position = savedPos;
        return true;
    }

    private static bool PatchExtensionList(FileStream fs, long offset, long size)
    {
        long savedPos = fs.Position;
        fs.Position = offset;
        var content = new byte[size];
        fs.ReadExactly(content, 0, (int)size);

        // Overwrite the Sentry GDExtension path with spaces (same byte count).
        var search = System.Text.Encoding.UTF8.GetBytes("res://addons/sentry/sentry.gdextension");
        int idx = FindBytes(content, search);
        if (idx < 0)
        {
            fs.Position = savedPos;
            return false;
        }

        for (int i = 0; i < search.Length; i++)
            content[idx + i] = (byte)' ';

        fs.Position = offset;
        fs.Write(content, 0, content.Length);
        fs.Position = savedPos;
        return true;
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    public void Dispose()
    {
        _cdnClient?.Dispose();
    }
}
