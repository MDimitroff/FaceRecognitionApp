using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.DataContracts;

namespace Demo
{
    public static class Functions
    {
        private static readonly string SubscriptionKey =
            Environment.GetEnvironmentVariable("SubscriptionKey", EnvironmentVariableTarget.Process);

        private static readonly string FaceApiUrl =
            Environment.GetEnvironmentVariable("FaceApiUrl", EnvironmentVariableTarget.Process);

        private static HttpClient HttpClient = new HttpClient();

        private static TelemetryClient TelemetryClient = new TelemetryClient(
            new TelemetryConfiguration(Environment.GetEnvironmentVariable("APP_INSIGHTS_KEY", EnvironmentVariableTarget.Process)));

        [FunctionName("upload")]
        public static async Task<HttpResponseMessage> UploadImage(
           [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequestMessage request,
           [OrchestrationClient] DurableOrchestrationClient client,
           ILogger log)
        {
            log.LogInformation("[POST] action to endpoint /upload started");
            DateTime start = DateTime.UtcNow;
            
            var serializedHttpContent = await (new HttpMessageContent(request).ReadAsByteArrayAsync());
            var instanceId = await client.StartNewAsync("StartOrchestrator", serializedHttpContent);
            var response = await client.WaitForCompletionOrCreateCheckStatusResponseAsync(request, instanceId, TimeSpan.FromSeconds(30), TimeSpan.Zero);

            // Track ApplicationInsights dependency
            var dependency = new DependencyTelemetry
            {
                Name = "POST image to upload API",
                Target = "FaceRecognitionApp",
                Data = "FaceRecognitionApp URL",
                Timestamp = start,
                Duration = DateTime.UtcNow - start,
                Success = response.StatusCode == System.Net.HttpStatusCode.OK
            };
            dependency.Context.User.Id = "[POST] /upload API";

            TelemetryClient.TrackDependency(dependency);
            log.LogInformation("[POST] action to endpoint /upload ended");

            return response;
        }

        [FunctionName("StartOrchestrator")]
        public static async Task<string> StartOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var serializedHttpContent = context.GetInput<byte[]>();

            var imageAsByteArray = await context.CallActivityAsync<byte[]>("GetFilesBytes", serializedHttpContent);
            var result = await context.CallActivityAsync<string>("ReadFaceEmotions", imageAsByteArray);

            return result;
        }

        [FunctionName("GetFilesBytes")]
        public static async Task<byte[]> GetFilesBytes([ActivityTrigger] byte[] serializedHttpContent)
        {
            var tmpRequest = new HttpRequestMessage();
            tmpRequest.Content = new ByteArrayContent(serializedHttpContent);
            tmpRequest.Content.Headers.Add("Content-Type", "application/http;msgtype=request");
            var deserializedHttpRequest = await tmpRequest.Content.ReadAsHttpRequestMessageAsync(); 

            var provider = new MultipartMemoryStreamProvider();
            await deserializedHttpRequest.Content.ReadAsMultipartAsync(provider);
            var file = provider.Contents.First();
            var fileData = await file.ReadAsByteArrayAsync();

            return fileData;
        }

        [FunctionName("ReadFaceEmotions")]
        public static async Task<string> ReadFaceEmotions([ActivityTrigger] byte[] imageAsByteArray)
        {
            string requestParameters = "returnFaceId=true&returnFaceLandmarks=false" +
                "&returnFaceAttributes=age,gender,smile,facialHair,glasses,emotion";

            string uri = $"{FaceApiUrl}?{requestParameters}";
            string jsonResult = string.Empty;

            using (var content = new ByteArrayContent(imageAsByteArray))
            {
                content.Headers.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var response = await HttpClient.PostAsync(uri, content);
                jsonResult = await response.Content.ReadAsStringAsync();
            }

            return jsonResult;
        }
    }
}