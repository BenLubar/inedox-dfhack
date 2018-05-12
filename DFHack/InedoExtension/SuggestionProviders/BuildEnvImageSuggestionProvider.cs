using System.Collections.Generic;
using System.Threading.Tasks;
using Inedo.Extensibility;
using Inedo.Web;

namespace Inedo.Extensions.DFHack.SuggestionProviders
{
    public sealed class BuildEnvImageSuggestionProvider : ISuggestionProvider
    {
        public Task<IEnumerable<string>> GetSuggestionsAsync(IComponentConfiguration config)
        {
            return Task.FromResult<IEnumerable<string>>(new[] { "latest", "gcc-4.8", "msvc" });
        }
    }
}
