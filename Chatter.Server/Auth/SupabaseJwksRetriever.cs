using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace Chatter.Server.Auth;

// Fetches Supabase's rotating public signing keys so JwtBearer can validate access
// tokens without a static shared secret. Used with ConfigurationManager<JsonWebKeySet>
// so the keys are cached and refreshed automatically.
// https://supabase.com/docs/guides/auth/jwts#json-web-key-sets-jwks
public class SupabaseJwksRetriever : IConfigurationRetriever<JsonWebKeySet>
{
    public async Task<JsonWebKeySet> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        return JsonWebKeySet.Create(json);
    }
}
