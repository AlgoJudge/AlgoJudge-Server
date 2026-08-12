using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AlgoJudge.Server.Api
{
    /// <summary>
    /// What a streamed upload's form actually contains.
    /// <para>
    /// <b>Needed because the parameters are gone.</b> An endpoint that reads its
    /// own body with <c>MultipartReader</c> declares no <c>[FromForm]</c>
    /// arguments — that is the point — and Swashbuckle infers a request body from
    /// arguments. Without this the three upload endpoints would appear in
    /// <c>openapi.json</c> as taking nothing at all, which is worse than the old
    /// document in the one place the Client most needs it to be right.
    /// </para>
    /// <para>
    /// So the shape is declared where it is served, next to the code that reads
    /// it, rather than maintained by hand in a committed document that CI only
    /// checks for equality with itself.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MultipartFormAttribute : Attribute
    {
        /// <summary>The name of the file part, if there is one.</summary>
        public string? File { get; init; }

        /// <summary>Whether a request without the file part is refused.</summary>
        public bool FileRequired { get; init; }

        /// <summary>The text fields beside it, in no particular order — order is not a contract.</summary>
        public string[] Fields { get; init; } = [];

        /// <summary>Which of <see cref="Fields"/> must be present.</summary>
        public string[] RequiredFields { get; init; } = [];
    }

    /// <summary>
    /// Puts the declared multipart shape back into the generated document.
    /// </summary>
    public sealed class MultipartFormOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var declared = context.MethodInfo
                .GetCustomAttributes(typeof(MultipartFormAttribute), inherit: false)
                .OfType<MultipartFormAttribute>()
                .FirstOrDefault();

            if (declared is null) return;

            var properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal);

            if (declared.File is { Length: > 0 } file)
            {
                properties[file] = new OpenApiSchema { Type = "string", Format = "binary" };
                if (declared.FileRequired) required.Add(file);
            }

            foreach (var field in declared.Fields)
            {
                properties[field] = new OpenApiSchema { Type = "string" };
            }

            foreach (var field in declared.RequiredFields) required.Add(field);

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = required.Count > 0,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Properties = properties,
                            Required = required,
                        },
                    },
                },
            };
        }
    }
}
