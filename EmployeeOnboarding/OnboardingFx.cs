using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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

        // Wait for events that complete the work document.
        var interviewTask =
            context.WaitForExternalEventAndSetCustomStatus<InterviewFeedback>(nameof(InterviewFeedback),
                "Interview feedback collected");

        var backgroundCheckTask =
            context.WaitForExternalEventAndSetCustomStatus<BackgroundCheckFeedback>(nameof(BackgroundCheckFeedback),
                "Background check feedback collected");

        var contractTask =
            context.WaitForExternalEventAndSetCustomStatus<ContractFeedback>(nameof(ContractFeedback),
                "Contract feedback collected");

        // Wait until we have all the required information of the work document
        await Task.WhenAll(interviewTask, backgroundCheckTask, contractTask);

        // Inform the approver that we have a document ready for submission
        await context.CallActivityAsync<bool>(
            nameof(SubmitDocument),
            new WorkDocument(
                workDocDetails,
                await interviewTask,
                await backgroundCheckTask,
                await contractTask));

        context.SetCustomStatus("Awaiting submission");

        // Record whether the approver accepted or rejected the document.
        var isSubmissionApproved = await context.WaitForExternalEvent<bool>("SubmissionApproval");

        context.SetCustomStatus("Submitted document");

        // Placeholder for code to submit the document.

        context.SetOutput($"Submitted: {isSubmissionApproved}");
    }

    [FunctionName(nameof(SubmitDocument))]
    public static bool SubmitDocument([ActivityTrigger] WorkDocument result, ILogger log)
    {
        log.LogInformation(
            "Work doc details: {Properties}. Interview feedback {InterviewFeedback}. Background check feedback {BackgroundCheckFeedback}. Contract feedback: {ContractFeedback}",
            result.Properties, result.InterviewFeedback, result.BackgroundCheckFeedback, result.ContractFeedback);

        // Placeholder for code to inform the approver with document details.

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

    [FunctionName(nameof(GetInstances))]
    public static async Task<IActionResult> GetInstances(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")]
        HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient client,
        ILogger log)
    {
        var filter = new OrchestrationStatusQueryCondition
        {
            RuntimeStatus = new[]
            {
                OrchestrationRuntimeStatus.Pending,
                OrchestrationRuntimeStatus.Running
            },
            CreatedTimeFrom = DateTime.UtcNow.Subtract(TimeSpan.FromDays(7)),
            PageSize = 100
        };

        var result = await client.ListInstancesAsync(filter, CancellationToken.None);
        return new OkObjectResult(result.DurableOrchestrationState);
    }


    public static Task<T> WaitForExternalEventAndSetCustomStatus<T>(
        this IDurableOrchestrationContext context, string name, string statusMessage)
    {
        var tcs = new TaskCompletionSource<T>();
        var waitForEventTask = context.WaitForExternalEvent<T>(name);

        // Chains a task that sets a custom status message once the event is complete
        waitForEventTask.ContinueWith(t =>
            {
                context.SetCustomStatus(statusMessage);
                tcs.SetResult(t.Result);
            }, TaskContinuationOptions.ExecuteSynchronously
        );

        return tcs.Task;
    }
}

// Metadata of work document
public record DocumentProperties(string Title, DateTimeOffset CreatedDate, string Creator, string ApplicationId);

public record InterviewFeedback(string Feedback, bool IsPassed);

public record BackgroundCheckFeedback(string Feedback, bool IsPassed);

public record ContractFeedback(string Feedback, bool IsPassed);

// Work document
public record WorkDocument(DocumentProperties Properties, InterviewFeedback InterviewFeedback,
    BackgroundCheckFeedback BackgroundCheckFeedback, ContractFeedback ContractFeedback);