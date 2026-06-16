using Google.Apis.Auth.OAuth2;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MundusVivens.Prototype.Services;

public interface IGoogleAuthService
{
    Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken = default);
}

public class GoogleAuthService : IGoogleAuthService
{
    private GoogleCredential? _cachedCredential;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);

    public async Task<string> GetGoogleAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedCredential == null)
        {
            await _credentialLock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedCredential == null)
                {
                    string jsonKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "google-credentials.json");
                    if (!File.Exists(jsonKeyPath))
                    {
                        throw new FileNotFoundException($"인증 키 파일을 찾을 수 없습니다: {jsonKeyPath}");
                    }
                    
                    string jsonContent = await File.ReadAllTextAsync(jsonKeyPath, cancellationToken);
                    var specificCredential = Google.Apis.Auth.OAuth2.CredentialFactory.FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(jsonContent);

                    _cachedCredential = specificCredential.ToGoogleCredential()
                        .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
                }
            }
            finally
            {
                _credentialLock.Release();
            }
        }

        return await ((ITokenAccess)_cachedCredential).GetAccessTokenForRequestAsync(authUri: null, cancellationToken: cancellationToken);
    }
}
