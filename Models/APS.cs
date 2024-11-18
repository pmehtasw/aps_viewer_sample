using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Authentication;
using Autodesk.Authentication.Model;
using Autodesk.Oss.Model;
using Autodesk.Oss;
using Autodesk.ModelDerivative.Model;
using Autodesk.ModelDerivative;
using Autodesk.DataManagement.Model;
using OssRegion = Autodesk.Oss.Model.Region;
using JobRegion = Autodesk.ModelDerivative.Model.Region;
using ModelDerivativeJob = Autodesk.ModelDerivative.Model.Job;


public partial class APS
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _bucket;

    public APS(string clientId, string clientSecret, string bucket = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _bucket = string.IsNullOrEmpty(bucket) ? string.Format("{0}-basic-app", _clientId.ToLower()) : bucket;
    }
}


/// <summary>
/// Auth methods
/// </summary>
public record Token(string AccessToken, DateTime ExpiresAt);

public partial class APS
{
    private Token _internalTokenCache;
    private Token _publicTokenCache;

    private async Task<Token> GetToken(List<Scopes> scopes)
    {
        var authenticationClient = new AuthenticationClient();
        var auth = await authenticationClient.GetTwoLeggedTokenAsync(_clientId, _clientSecret, scopes);
        return new Token(auth.AccessToken, DateTime.UtcNow.AddSeconds((double)auth.ExpiresIn));
    }

    public async Task<Token> GetPublicToken()
    {
        if (_publicTokenCache == null || _publicTokenCache.ExpiresAt < DateTime.UtcNow)
            _publicTokenCache = await GetToken([Scopes.ViewablesRead]);
        return _publicTokenCache;
    }

    private async Task<Token> GetInternalToken()
    {
        if (_internalTokenCache == null || _internalTokenCache.ExpiresAt < DateTime.UtcNow)
            _internalTokenCache = await GetToken([Scopes.BucketCreate, Scopes.BucketRead, Scopes.DataRead, Scopes.DataWrite, Scopes.DataCreate]);
        return _internalTokenCache;
    }
}

/// <summary>
/// Object-Storage methods
/// </summary>
public partial class APS
{
    private async Task EnsureBucketExists(string bucketKey)
    {
        var auth = await GetInternalToken();
        var ossClient = new OssClient();
        try
        {
            await ossClient.GetBucketDetailsAsync(bucketKey, accessToken: auth.AccessToken);
        }
        catch (OssApiException ex)
        {
            if (ex.HttpResponseMessage.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var payload = new CreateBucketsPayload
                {
                    BucketKey = bucketKey,
                    PolicyKey = PolicyKey.Persistent
                };
                await ossClient.CreateBucketAsync(OssRegion.US, payload, auth.AccessToken);
            }
            else
            {
                throw;
            }
        }
    }

    public async Task<ObjectDetails> UploadModel(string objectName, Stream stream)
    {
        await EnsureBucketExists(_bucket);
        var auth = await GetInternalToken();
        var ossClient = new OssClient();
        var objectDetails = await ossClient.Upload(_bucket, objectName, stream, accessToken: auth.AccessToken);
        return objectDetails;
    }

    public async Task<IEnumerable<ObjectDetails>> GetObjects()
    {
        await EnsureBucketExists(_bucket);
        var auth = await GetInternalToken();
        var ossClient = new OssClient();
        const int PageSize = 64;
        var results = new List<ObjectDetails>();
        var response = await ossClient.GetObjectsAsync(_bucket, PageSize, accessToken: auth.AccessToken);
        results.AddRange(response.Items);
        while (!string.IsNullOrEmpty(response.Next))
        {
            var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(response.Next).Query);
            response = await ossClient.GetObjectsAsync(_bucket, PageSize, startAt: queryParams["startAt"], accessToken: auth.AccessToken);
            results.AddRange(response.Items);
        }
        return results;
    }
}

/// <summary>
/// Model-Derivative methods
/// </summary>
public record TranslationStatus(string Status, string Progress, IEnumerable<string> Messages);

public partial class APS
{
    public static string Base64Encode(string plainText)
    {
        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return System.Convert.ToBase64String(plainTextBytes).TrimEnd('=');
    }

    public async Task<ModelDerivativeJob> TranslateModel(string objectId, string rootFilename)
    {
        var auth = await GetInternalToken();
        var modelDerivativeClient = new ModelDerivativeClient();
        var payload = new JobPayload
        {
            Input = new JobPayloadInput
            {
                Urn = Base64Encode(objectId)
            },
            Output = new JobPayloadOutput
            {
                Formats =
                [
                    new JobPayloadFormatSVF2
                    {
                        Views = [View._2d, View._3d]
                    }
                ]
            }
        };
        if (!string.IsNullOrEmpty(rootFilename))
        {
            payload.Input.RootFilename = rootFilename;
            payload.Input.CompressedUrn = true;
        }
        var job = await modelDerivativeClient.StartJobAsync(jobPayload: payload, region: JobRegion.US, accessToken: auth.AccessToken);
        return job;
    }

    public async Task<TranslationStatus> GetTranslationStatus(string urn)
    {
        var auth = await GetInternalToken();
        var modelDerivativeClient = new ModelDerivativeClient();
        try
        {
            var manifest = await modelDerivativeClient.GetManifestAsync(urn, accessToken: auth.AccessToken);
            return new TranslationStatus(manifest.Status, manifest.Progress, []);
        }
        catch (ModelDerivativeApiException ex)
        {
            if (ex.HttpResponseMessage.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new TranslationStatus("n/a", "", null);
            }
            else
            {
                throw;
            }
        }
    }
}