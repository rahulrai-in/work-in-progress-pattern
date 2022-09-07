using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace EmployeeOnboarding;

public static class OnboardingFx
{
    [FunctionName(nameof(StartWorkflow))]
    public static async Task StartWorkflow(
        [OrchestrationTrigger] IDurableOrchestrationContext context)
    {
        var workDocDetails = context.GetInput<DocumentProperties>();
        context.SetCustomStatus("Waiting for feedback");

        var interviewTask = context.WaitForExternalEvent<InterviewFeedback>("InterviewFeedbackResponse")
            .ContinueWith(task =>
            {
                context.SetCustomStatus("Interview feedback collected");
                return task;
            });

        var backgroundCheckTask = context.WaitForExternalEvent<BackgroundCheckFeedback>("BackgroundFeedbackResponse")
            .ContinueWith(task =>
            {
                context.SetCustomStatus("Background check feedback collected");
                return task;
            });

        var contractTask = context.WaitForExternalEvent<ContractFeedback>("ContractFeedbackResponse")
            .ContinueWith(task =>
            {
                context.SetCustomStatus("Contract feedback collected");
                return task;
            });

        await Task.WhenAll(interviewTask, backgroundCheckTask, contractTask);

        await context.CallActivityAsync<bool>(
            nameof(SubmitDocument),
            (
                workDocDetails,
                await interviewTask.Result,
                await backgroundCheckTask.Result,
                await contractTask.Result
            ));

        context.SetCustomStatus("Awaiting submission");

        var isSubmissionApproved = await context.WaitForExternalEvent<bool>("SubmissionApproval");

        context.SetCustomStatus("Submitted document");

        // Placeholder for code to submit the document.

        context.SetOutput($"Submitted: {isSubmissionApproved}");
    }

    [FunctionName(nameof(SubmitDocument))]
    public static bool SubmitDocument(
        [ActivityTrigger]
        (DocumentProperties workDocDetails, InterviewFeedback interviewFeedback, BackgroundCheckFeedback
            backgroundFeedback,
            ContractFeedback contractFeedback) result, ILogger log)
    {
        log.LogInformation(
            $"Work doc details: {result.workDocDetails}. Interview feedback {result.interviewFeedback}. Background check feedback {result.backgroundFeedback}. Contract feedback: {result.contractFeedback}");

        // Placeholder for code to inform the approver with details.

        return true;
    }

    [FunctionName(nameof(Start))]
    public static async Task<HttpResponseMessage> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")]
        HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient starter,
        ILogger log)
    {
        var data = await req.Content.ReadAsAsync<DocumentProperties>();
        var instanceId = await starter.StartNewAsync(nameof(StartWorkflow), data);
        return starter.CreateCheckStatusResponse(req, instanceId);
    }
}

public record DocumentProperties(string Title, DateTimeOffset CreatedDate, string Creator, string ApplicationId);

public record InterviewFeedback(string Feedback, bool IsPassed);

public record BackgroundCheckFeedback(string Feedback, bool IsPassed);

public record ContractFeedback(string Feedback, bool IsPassed);

public record WorkDocument(DocumentProperties Properties, InterviewFeedback InterviewFeedback,
    BackgroundCheckFeedback BackgroundCheckFeedback, ContractFeedback ContractFeedback);