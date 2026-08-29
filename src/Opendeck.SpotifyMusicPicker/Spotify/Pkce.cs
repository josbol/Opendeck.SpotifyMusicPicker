using System.Security.Cryptography;
using System.Text;

namespace Opendeck.SpotifyMusicPicker.Spotify;

/// <summary>RFC 7636 helpers for the authorization-code-with-PKCE flow (no client secret needed).</summary>
public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(12));
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
