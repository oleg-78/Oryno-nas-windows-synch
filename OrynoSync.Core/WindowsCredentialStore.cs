using System.Security.Cryptography;
using System.Text;

namespace OrynoSync.Core;

public sealed class WindowsCredentialStore(string directory) : ICredentialStore
{
    private readonly byte[] _entropy=Encoding.UTF8.GetBytes("Oryno Sync Windows credential v1");
    private string PathFor(string account){var name=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account))).ToLowerInvariant();return Path.Combine(directory,name+".credential");}
    public async Task SaveAsync(string account,string secret,CancellationToken ct=default)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Windows DPAPI is required.");Directory.CreateDirectory(directory);var clear=Encoding.UTF8.GetBytes(secret);try{var encrypted=ProtectedData.Protect(clear,_entropy,DataProtectionScope.CurrentUser);await File.WriteAllBytesAsync(PathFor(account),encrypted,ct);}finally{CryptographicOperations.ZeroMemory(clear);}
    }
    public async Task<string?> ReadAsync(string account,CancellationToken ct=default)
    {
        var path=PathFor(account);if(!File.Exists(path))return null;var encrypted=await File.ReadAllBytesAsync(path,ct);var clear=ProtectedData.Unprotect(encrypted,_entropy,DataProtectionScope.CurrentUser);try{return Encoding.UTF8.GetString(clear);}finally{CryptographicOperations.ZeroMemory(clear);}
    }
    public Task DeleteAsync(string account,CancellationToken ct=default){var p=PathFor(account);if(File.Exists(p))File.Delete(p);return Task.CompletedTask;}
}

public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string,string> _values=[];
    public Task SaveAsync(string account,string secret,CancellationToken ct=default){_values[account]=secret;return Task.CompletedTask;}
    public Task<string?> ReadAsync(string account,CancellationToken ct=default)=>Task.FromResult(_values.GetValueOrDefault(account));
    public Task DeleteAsync(string account,CancellationToken ct=default){_values.Remove(account);return Task.CompletedTask;}
}
