using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace SXMPlayer
{
    internal class CacheManager
    {
        private readonly List<(string FileName,DateTimeOffset Expiry)> allTracked = new List<(string FileName, DateTimeOffset Expiry)>();
        private string cacheFolder;
        private ILogger<CacheManager> logger;
        private bool initialized;
        private DateTimeOffset lastCacheExpunge;

        public CacheManager(string cacheFolder, ILoggerFactory loggerFactory)
        {
            this.cacheFolder = cacheFolder;
            this.logger = loggerFactory.CreateLogger<CacheManager>();
        }

        private static string GetCacheId(string filename)
        {
            //return the last part of the filename
            var lastPath = filename.Split(['/','\\']).Last();
            return Path.GetFileName(filename);
        }

        public async Task<byte[]?> GetCachedFile(string filename)
        {
            var path = Path.Combine(cacheFolder, GetCacheId(filename));
            if (File.Exists(path))
            {
                return await File.ReadAllBytesAsync(path);
            }
            return null;
        }

        private void ExpireFilesIfNecessary()
        {
            if (lastCacheExpunge > DateTimeOffset.Now.AddMinutes(-10))
                return;
            var now = DateTimeOffset.Now;
            var expired = allTracked.Where(x => x.Expiry < now).ToList();
            foreach (var file in expired)
            {
                try
                {
                    File.Delete(file.FileName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error deleting file {file}", file.FileName);
                }
            }
            allTracked.RemoveAll(x => x.Expiry < now);
            lastCacheExpunge = now;
        }

        public async Task SaveFile(string filename, byte[] content, DateTimeOffset expiry)
        {
            if (!initialized)
            {
                InitCache();
            }
            var path = Path.Combine(cacheFolder, GetCacheId(filename));
            var tracker = (path, expiry);
            allTracked.Add((path, expiry));
            await File.WriteAllBytesAsync(path, content);
            ExpireFilesIfNecessary();
        }

        private void InitCache()
        {
            if (!Directory.Exists(cacheFolder))
                Directory.CreateDirectory(cacheFolder);
            else
            {
                var files = Directory.GetFiles(cacheFolder);
                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }
            initialized = true;
        }

    }
}