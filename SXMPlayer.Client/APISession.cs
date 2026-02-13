using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SXMPlayer;


public record class DeviceInfo(string DeviceId, string DeviceGrant);

public record class SXMTokens(string AnonAccessToken, DateTimeOffset AnonExpiry, string? IdentityGrant);

public record class SXMAccessToken(string AccessToken, string AccessTokenId, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt, string SessionType);

public record class SXMSegment(SXMStream stream, string segment);

public record class SXMStream(string token, string channel, string stream, string data, string path);

public static class APITools
{
    public static SXMAccessToken AsSXMToken(this SessionData accessToken) => new SXMAccessToken(accessToken.AccessToken!, accessToken.AccessTokenId!, DateTimeOffset.Parse(accessToken.AccessTokenExpiresAt!), DateTimeOffset.Parse(accessToken.RefreshTokenExpiresAt!), accessToken.SessionType!);
    //public static SXMChannel AsSXMChannel(this items5 channel) => new SXMChannel() { 
    //    Id = channel.Entity.Id, Name = channel.Entity.Texts.Title.Default, Description = channel.Entity.Texts.Description.Default };
    
    public static string MakeFileName(string channelName)
    {
        // convert channel name to a valid file name and limit to 15 characters
        var fName = string.Join("_", channelName.ToLowerInvariant().Split(Path.GetInvalidFileNameChars()));
        //remove apostrophes and other characters
        fName = fName.Replace("'", "");
        fName = fName.Replace(" ", "_");
        fName = fName.Substring(0, Math.Min(15, fName.Length));
        return fName;
    }
}

public class APISession
{
    private HttpClientHandler _session = null!;
    private CookieContainer _CookieContainer = null!;
    private HttpClient _client = null!;
    private Uri baseAddress;
    private ILogger<APISession> logger;

    private readonly string accessFile;
    private readonly string deviceFile;
    private readonly string tokensFile;
    private readonly string username;
    private readonly string password;

    private SemaphoreSlim _loginSemaphore = new SemaphoreSlim(1, 1);

    public DeviceInfo? Device { get; private set; }
    public SXMTokens? Tokens { get; set; }

    private Guid Guid { get; set; } = Guid.NewGuid();
    public SXMAccessToken? AccessToken { get; private set; }
    public Client apiClient { get; private set; } = null!;

    public APISession(string baseAddress, ILoggerFactory loggerFactory, string contentRootPath, string username, string password)
    {
        this.baseAddress = new Uri(baseAddress);
        this.logger = loggerFactory.CreateLogger<APISession>();
        deviceFile = Path.Combine(contentRootPath, "device.json");
        tokensFile = Path.Combine(contentRootPath, "tokens.json");
        accessFile = Path.Combine(contentRootPath, "access.json");
        this.username = username;
        this.password = password;
    }

    private async Task<bool> ClearCache(bool clearDevice)
    {
        if (clearDevice)
        {
            File.Delete(deviceFile);
            File.Delete(tokensFile);
        }
        File.Delete(accessFile);
        await ClearAccessToken();
        return true;
    }

    internal async Task ReLogin()
    {
        await ClearCache(false);
        await LoginIfNecessary();
    }

    internal async Task<bool> LoginIfNecessary(bool clearDevice = false, int retryCount = 0)
    {
        await _loginSemaphore.WaitAsync();
        try
        {
            return await LoginIfNecessaryInternal(clearDevice, retryCount);
        }
        finally
        {
            _loginSemaphore.Release();
        }
    }

    private async Task<bool> LoginIfNecessaryInternal(bool clearDevice = false, int retryCount = 0)
    {
        try
        {
            if (retryCount >= 5)
            {
                logger.LogError($"Too many retries");
                throw new InvalidOperationException("Too many retries");
            }
            CheckTokenExpiry();

            if (AccessToken != null)
            {
                logger.LogDebug($"Using existing access token");
                return true;
            }
            apiClient = new Client(GetHttpClient());
            apiClient.SetSession(this);
            DeviceInfo? deviceInfo = await Tools.ReadJsonFile<DeviceInfo>(deviceFile, logger);
            CheckTokenExpiry();
            if (deviceInfo != null)
            {
                logger.LogInformation($"Using existing device {deviceInfo.DeviceId}");
            }
            else
            {
                var devAttributes = new DeviceAttributes2
                {
                    Browser = new Browser2
                    {
                        BrowserVersion = "121.0.0.0",
                        Browser = "Edge",
                        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
                        Sdk = "web",
                        App = "web",
                        SdkVersion = "121.0.0.0",
                        AppVersion = "121.0.0.0"
                    },

                };
                var device = await apiClient.DevicesAsync(new Body8() { DevicePlatform = "web-desktop", DeviceAttributes = devAttributes });
                deviceInfo = new DeviceInfo(device.DeviceId!, device.Grant!);
                await Tools.WriteObject(deviceInfo, deviceFile, logger);
                logger.LogInformation($"Created new device {deviceInfo.DeviceId}");
            }
            SetDevice(deviceInfo);
            Tokens = await Tools.ReadJsonFile<SXMTokens>(tokensFile, logger);
            CheckTokenExpiry();
            if (Tokens != null)
            {
                logger.LogInformation($"Using existing tokens - expiry is {Tokens.AnonExpiry}");
            }
            else
            {
                logger.LogInformation($"Logging anonymous");
                var anon = await apiClient.AnonymousAsync("");
                SetAnonymousAccessToken(anon.AccessToken!, DateTimeOffset.Parse(anon.AccessTokenExpiresAt!));
                var authData = new Body7 { Handle = username, Password = password };
                logger.LogInformation($"Logging with user/password");
                var response = await apiClient.PasswordAsync(authData);
                SetIdentityGrant(response.Grant!);
                await Tools.WriteObject(Tokens, tokensFile, logger);
            }
            var accessToken = await Tools.ReadJsonFile<SXMAccessToken>(accessFile, logger);
            if (accessToken != null)
                SetIdentityToken(accessToken);

            CheckTokenExpiry();

            if (AccessToken != null)
            {
                logger.LogDebug($"Using existing access token - expiry is {AccessToken.AccessTokenExpiresAt}");
            }
            else
            {
                logger.LogInformation($"Creating access token");
                var authenticatedData = await apiClient.AuthenticatedAsync("");
                logger.LogInformation($"Created access token");
                SetIdentityToken(authenticatedData.AsSXMToken());
                await Tools.WriteObject(AccessToken, accessFile, logger);
            }
            logger.LogInformation($"Logged in");
            return true;
        }
        catch (ApiException ex)
        {
            if (ex.StatusCode == 401)
            {
                await ClearCache(clearDevice);
                if (retryCount > 0)
                    clearDevice = true;
                logger.LogError($"Login forbidden error - clearing cache - retryCount={retryCount} clearDevice={clearDevice}");
                await Task.Delay(TimeSpan.FromSeconds(10));
                return await LoginIfNecessaryInternal(clearDevice, retryCount + 1);
            }
            else if (ex.StatusCode == 500)
            {
                await ClearCache(clearDevice);
                if (retryCount > 0)
                    clearDevice = true;
                logger.LogError($"SXM Error - clearing cache - retryCount={retryCount} clearDevice={clearDevice} - pausing for 5 minutes");
                await Task.Delay(TimeSpan.FromMinutes(5));
                return await LoginIfNecessaryInternal(clearDevice, retryCount + 1);
            }
            else
            {
                logger.LogError(ex, $"Error logging in");
                throw;
            }
        }
    }


    public string GetKey()
    {
        //encode in base 64 the guid converted to string            
        return Convert.ToBase64String(UTF8Encoding.UTF8.GetBytes(Guid.ToString()));
    }


    public async Task ClearAccessToken()
    {
        _CookieContainer = new CookieContainer();
        AccessToken = null;
    }

    public HttpClient GetHttpClient()
    {
        if (_client != null)
            return _client;
        _session = new HttpClientHandler();
        if (_CookieContainer is null)
            _CookieContainer = new CookieContainer();
        _client = new HttpClient(_session) 
        { 
            Timeout = TimeSpan.FromSeconds(60) // 60s per attempt with Polly handling retries
        };
        if (baseAddress is null)
            throw new InvalidCastException("Invalid Base Address");
        _client.BaseAddress = baseAddress;
        return _client;
    }

    public void SetDevice(DeviceInfo deviceInfo)
    {
        Device = deviceInfo;
    }

    public void SetAnonymousAccessToken(string accessToken, DateTimeOffset expiry)
    {
        Tokens = new SXMTokens(accessToken, expiry, null);            
    }

    public void SetIdentityGrant(string grant)
    {
        if (Tokens == null)
            throw new InvalidCastException("Invalid Auth Flow");
        else
            Tokens = Tokens with { IdentityGrant = grant };
    }

    public void SetIdentityToken(SXMAccessToken accessToken)
    {
        AccessToken = accessToken;
    }

    public void CheckTokenExpiry()
    {
        var expiryTime = DateTimeOffset.UtcNow;
        if (AccessToken != null && (AccessToken.AccessTokenExpiresAt.ToUniversalTime() - expiryTime).TotalMinutes < 10)
        {                
            logger.LogInformation("User token expiring in 10 minutes, refreshing");
            AccessToken = null;
        }
        if (Tokens != null && (Tokens.AnonExpiry.ToUniversalTime() - expiryTime).TotalMinutes < 10)
        {
            logger.LogInformation("Anonymous token expiring in 10 minutes, refreshing");
            Tokens = null;
        }
    }
}
