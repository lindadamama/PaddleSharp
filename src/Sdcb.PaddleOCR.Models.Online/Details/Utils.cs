﻿using SharpCompress.Archives;
using SharpCompress.Archives.GZip;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Sdcb.PaddleOCR.Models.Online.Details;

internal static class Utils
{
    public static Task DownloadFile(Uri uri, string localFile, CancellationToken cancellationToken) => DownloadFiles(new Uri[] { uri }, localFile, cancellationToken);

    public static async Task DownloadFiles(Uri[] uris, string localFile, CancellationToken cancellationToken)
    {
        using HttpClient http = new();

        foreach (Uri uri in uris)
        {
            try
            {
                HttpResponseMessage resp = await http.GetAsync(uri, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to download: {uri}, status code: {(int)resp.StatusCode}({resp.StatusCode})");
                    continue;
                }

                using (FileStream file = File.OpenWrite(localFile))
                {
                    await resp.Content.CopyToAsync(file/*, cancellationToken*/);
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Failed to download: {uri}, {ex}");
                continue;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine($"Failed to download: {uri}, timeout.");
                continue;
            }
        }

        throw new Exception($"Failed to download {localFile} from all uris: {string.Join(", ", uris.Select(x => x.ToString()))}");
    }

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new();

    public static async Task DownloadAndExtractAsync(
        string name, Uri uri, string rootDir, CancellationToken cancellationToken)
    {
        string paramsFile = Path.Combine(rootDir, "inference.pdiparams");
        if (File.Exists(paramsFile))
            return;

        string key = name;
        SemaphoreSlim gate = _modelLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(paramsFile))
                return;

            Directory.CreateDirectory(rootDir);
            string localTarFile = Path.Combine(rootDir, uri.Segments.Last());

            if (!File.Exists(localTarFile) || new FileInfo(localTarFile).Length == 0)
            {
                Console.WriteLine($"Downloading {name} model from {uri}");
                await DownloadFile(uri, localTarFile, cancellationToken);
            }

            Console.WriteLine($"Extracting {localTarFile} to {rootDir}");
            using (IArchive archive = ArchiveFactory.Open(localTarFile))
            {
                if (archive is GZipArchive)
                {
                    using Stream stream = archive.Entries.Single().OpenEntryStream();
                    using MemoryStream ms = new();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    IArchive inner = ArchiveFactory.Open(ms);
                    inner.WriteToDirectory(rootDir);
                }
                else
                {
                    archive.WriteToDirectory(rootDir);
                }

                CheckLocalOCRModel(rootDir);
            }

            File.Delete(localTarFile);
        }
        finally
        {
            gate.Release();

            if (gate.CurrentCount == 1)
                _modelLocks.TryRemove(key, out _);
        }
    }

    public static void CheckLocalOCRModel(string rootDir)
    {
        string[] filesToCheck = new[]
        {
            Path.Combine(rootDir, "inference.pdiparams"),
        };

        foreach (string path in filesToCheck)
        {
            string fileName = Path.GetFileName(path);

            if (!File.Exists(path))
            {
                throw new Exception($"{fileName} not found in {rootDir}, model error?");
            }

            if (new FileInfo(path).Length == 0)
            {
                throw new Exception($"{fileName} invalid(length = 0), model error?");
            }
        }
    }

    public readonly static Type RootType = typeof(Settings);
    public readonly static Assembly RootAssembly = typeof(Settings).Assembly;
}
