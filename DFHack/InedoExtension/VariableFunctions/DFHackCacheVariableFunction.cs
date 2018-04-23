using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensibility.VariableFunctions;
using Inedo.IO;
using System.Threading.Tasks;

namespace Inedo.Extensions.DFHack.VariableFunctions
{
    [ScriptAlias(Name)]
    [ExtensionConfigurationVariable(Required = true)]
    public sealed class DFHackCacheVariableFunction : ScalarVariableFunction
    {
        private const string Name = "DFHackCache";

        protected override object EvaluateScalar(IVariableFunctionContext context) => string.Empty;

        public static async Task<string> GetAsync(IOperationExecutionContext context) => (await context.ExpandVariablesAsync("$" + Name)).AsString();
        public static async Task<string> GetGitAsync(IOperationExecutionContext context) => PathEx.Combine(await GetAsync(context), "git");
        public static async Task<string> GetDFHackBinAsync(IOperationExecutionContext context) => PathEx.Combine(await GetAsync(context), "dfhack-bin");
    }
}
