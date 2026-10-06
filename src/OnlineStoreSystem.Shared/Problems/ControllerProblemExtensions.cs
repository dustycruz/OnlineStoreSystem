// File: src/OnlineStoreSystem.Shared/Problems/ControllerProblemExtensions.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace OnlineStoreSystem.Shared.Problems;

/// <summary>Concise helpers so controllers always answer errors with RFC 7807 bodies.</summary>
public static class ControllerProblemExtensions
{
    extension(ControllerBase controller)
    {
        /// <summary>404 problem details for a missing resource.</summary>
        public ObjectResult NotFoundProblem(string resource, object id) =>
            controller.Problem(
                statusCode: StatusCodes.Status404NotFound,
                type: ProblemTypes.NotFound,
                title: $"{resource} not found",
                detail: $"{resource} '{id}' does not exist.");

        /// <summary>409 problem details.</summary>
        public ObjectResult ConflictProblem(string title, string detail, string type = ProblemTypes.Conflict) =>
            controller.Problem(
                statusCode: StatusCodes.Status409Conflict,
                type: type,
                title: title,
                detail: detail);

        /// <summary>
        /// Problem details with additional extension members (e.g. <c>shortages</c>, <c>declineCode</c>).
        /// Created through <see cref="ControllerBase.ProblemDetailsFactory"/> so the global customisation
        /// (correlationId, traceId, service, timestamp) is applied as well.
        /// </summary>
        public ObjectResult ProblemWithExtensions(
            int statusCode,
            string type,
            string title,
            string? detail,
            IEnumerable<KeyValuePair<string, object?>> extensions)
        {
            var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
                controller.HttpContext, statusCode: statusCode, title: title, type: type, detail: detail);

            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }

            return new ObjectResult(problem) { StatusCode = statusCode };
        }

        /// <summary>400 validation problem details for errors detected after model binding.</summary>
        public ActionResult ValidationProblemFor(IDictionary<string, string[]> errors, string? detail = null)
        {
            var modelState = new ModelStateDictionary();
            foreach (var (key, messages) in errors)
            {
                foreach (var message in messages)
                {
                    modelState.AddModelError(key, message);
                }
            }

            return controller.ValidationProblem(detail: detail, modelStateDictionary: modelState);
        }
    }
}
