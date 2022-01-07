// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using CallingBotSample.Interfaces;
using CallingBotSample.Utility;
using CallingMeetingBot.Extenstions;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Auth;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Core.Notifications;
using Microsoft.Graph.Communications.Core.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CallingBotSample.Bots
{
	public class CallingBot : ActivityHandler
	{
		private readonly IConfiguration configuration;
		public IGraphLogger GraphLogger { get; }

		private IRequestAuthenticationProvider AuthenticationProvider { get; }

		private INotificationProcessor NotificationProcessor { get; }
		private CommsSerializer Serializer { get; }
		private readonly BotOptions options;

		private readonly ICard card;
		private readonly IGraph graph;
		private readonly IGraphServiceClient graphServiceClient;
		private static int step = 0;
		private static string botCallId = string.Empty;
		private static string botTenantId = string.Empty;
		private static Guid generalId = new Guid();




		public CallingBot(BotOptions options, IConfiguration configuration, ICard card, IGraph graph, IGraphServiceClient graphServiceClient, IGraphLogger graphLogger)
		{
			this.options = options;
			this.configuration = configuration;
			this.card = card;
			this.graph = graph;
			this.graphServiceClient = graphServiceClient;
			this.GraphLogger = graphLogger;

			var name = this.GetType().Assembly.GetName().Name;
			this.AuthenticationProvider = new AuthenticationProvider(name, options.AppId, options.AppSecret, graphLogger);
			this.Serializer = new CommsSerializer();
			this.NotificationProcessor = new NotificationProcessor(Serializer);
			this.NotificationProcessor.OnNotificationReceived += this.NotificationProcessor_OnNotificationReceived;
		}

		/// <summary>
		/// Process "/callback" notifications asyncronously. 
		/// </summary>
		/// <param name="request"></param>
		/// <param name="response"></param>
		/// <returns></returns>
		public async Task ProcessNotificationAsync(
			HttpRequest request,
			HttpResponse response)
		{
			try
			{
				var httpRequest = request.CreateRequestMessage();
				var results = await this.AuthenticationProvider.ValidateInboundRequestAsync(httpRequest).ConfigureAwait(false);
				if (results.IsValid)
				{
					var httpResponse = await this.NotificationProcessor.ProcessNotificationAsync(httpRequest).ConfigureAwait(false);
					await httpResponse.CreateHttpResponseAsync(response).ConfigureAwait(false);
				}
				else
				{
					var httpResponse = httpRequest.CreateResponse(HttpStatusCode.Forbidden);
					await httpResponse.CreateHttpResponseAsync(response).ConfigureAwait(false);
				}
			}
			catch (Exception e)
			{
				response.StatusCode = (int)HttpStatusCode.InternalServerError;
				await response.WriteAsync(e.ToString()).ConfigureAwait(false);
			}
		}

		protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
		{
			var credentials = new MicrosoftAppCredentials(this.configuration[Common.Constants.MicrosoftAppIdConfigurationSettingsKey], this.configuration[Common.Constants.MicrosoftAppPasswordConfigurationSettingsKey]);
			ConversationReference conversationReference = null;
			foreach (var member in membersAdded)
			{

				if (member.Id != turnContext.Activity.Recipient.Id)
				{
					await SendCardActions(turnContext, member, cancellationToken, conversationReference);
				}
			}
		}

		protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
		{
			//turnContext.Activity.From.AadObjectId;
			// I think with this we can get Caller Id dynamically
			var members = await TeamsInfo.GetMembersAsync(turnContext);
            switch (step)
            {
				case 0:
					if (string.IsNullOrEmpty(turnContext.Activity.Text))
					{
						dynamic value = turnContext.Activity.Value;
						if (value != null)
						{
							string type = value["type"];
							type = string.IsNullOrEmpty(type) ? "." : type.ToLower();
							await SendReponse(turnContext, type, cancellationToken);
						}
					}
					else
					{
						await SendCardActions(turnContext, members.FirstOrDefault(), cancellationToken);

						await SendReponse(turnContext, turnContext.Activity.Text.Trim().ToLower(), cancellationToken);
					}
					break;
				case 1:
					await turnContext.SendActivityAsync(MessageFactory.Attachment(this.card.GetSubMenuCardAttachment()));
					await BotSubMenuAsync(botCallId, botTenantId, generalId);
					break;
				case 2:
					dynamic value1 = turnContext.Activity.Value;

					string type1 = value1["type"];
					type1 = string.IsNullOrEmpty(type1) ? "2" : type1.ToLower();
					if (type1 == "1")
                    {
						await turnContext.SendActivityAsync("Para preguntas relacionadas con el periodo de cumplimiento marque 1.");
						await turnContext.SendActivityAsync("Para preguntas relacionadas con el cumplimiento en otras jurisdicciones, marque 2.");
						await turnContext.SendActivityAsync("Para preguntas relacionadas con excepciones o exoneraciones, marque 3.");
						await PrimeraOpcionAsync(botCallId, botTenantId, generalId);
					}
					else
                    {
						await SegundaOpcionAsync(botCallId, botTenantId, generalId);

					}
					step = 0;
					//preguntas
					break;
            }
			
		}

		private async Task SendCardActions(ITurnContext turnContext, ChannelAccount member, CancellationToken cancellationToken, ConversationReference conversationReference = null)
		{
			var credentials = new MicrosoftAppCredentials(this.configuration[Common.Constants.MicrosoftAppIdConfigurationSettingsKey], this.configuration[Common.Constants.MicrosoftAppPasswordConfigurationSettingsKey]);
			var proactiveMessage = MessageFactory.Attachment(this.card.GetWelcomeCardAttachment());
			proactiveMessage.TeamsNotifyUser();
			var conversationParameters = new ConversationParameters
			{
				IsGroup = false,
				Bot = turnContext.Activity.Recipient,
				Members = new ChannelAccount[] { member },
				TenantId = turnContext.Activity.Conversation.TenantId
			};
			await ((BotFrameworkAdapter)turnContext.Adapter).CreateConversationAsync(
				turnContext.Activity.TeamsGetChannelId(),
				turnContext.Activity.ServiceUrl,
				credentials,
				conversationParameters,
				async (t1, c1) =>
				{
					conversationReference = t1.Activity.GetConversationReference();
					await ((BotFrameworkAdapter)turnContext.Adapter).ContinueConversationAsync(
						configuration[Common.Constants.MicrosoftAppIdConfigurationSettingsKey],
						conversationReference,
						async (t2, c2) =>
						{
							await t2.SendActivityAsync(proactiveMessage, c2);
						},
						cancellationToken);
				},
				cancellationToken);
		}

		private async Task SendReponse(ITurnContext<IMessageActivity> turnContext, string input, CancellationToken cancellationToken)
		{
			switch (input)
			{
				case "createcall":
					var call = await graph.CreateCallAsync();
					if (call != null)
					{
						await turnContext.SendActivityAsync("Llamando...");//menu
						await turnContext.SendActivityAsync(MessageFactory.Attachment(this.card.GetMenuCardAttachment()));
					}
					break;
				case "transfercall":
					var sourceCallResponse = await graph.CreateCallAsync();
					if (sourceCallResponse != null)
					{
						await turnContext.SendActivityAsync("Transferring the call!");
						await graph.TransferCallAsync(sourceCallResponse.Id);
					}
					break;
				case "joinscheduledmeeting":
					var onlineMeeting = await graph.CreateOnlineMeetingAsync();
					if (onlineMeeting != null)
					{
						var statefullCall = await graph.JoinScheduledMeeting(onlineMeeting.JoinWebUrl);
						if (statefullCall != null)
						{
							await turnContext.SendActivityAsync($"[Click here to Join the meeting]({onlineMeeting.JoinWebUrl})");
						}
					}
					break;
				case "inviteparticipant":
					var meeting = await graph.CreateOnlineMeetingAsync();
					if (meeting != null)
					{
						var statefullCall = await graph.JoinScheduledMeeting(meeting.JoinWebUrl);
						if (statefullCall != null)
						{

							graph.InviteParticipant(statefullCall.Id);
							await turnContext.SendActivityAsync("Invited participant successfuly");
						}
					}
					break;
				case "menu":
					await turnContext.SendActivityAsync(MessageFactory.Attachment(card.GetMenuCardAttachment()));
					break;
				default:
					await turnContext.SendActivityAsync("Nuestro horario de servicio es de 8:30am a 5:00pm, de lunes a viernes.");
					break;
			}
		}

		private void NotificationProcessor_OnNotificationReceived(NotificationEventArgs args)
		{
			_ = NotificationProcessor_OnNotificationReceivedAsync(args).ForgetAndLogExceptionAsync(
			  this.GraphLogger,
			  $"Error processing notification {args.Notification.ResourceUrl} with scenario {args.ScenarioId}");
		}

		private async Task NotificationProcessor_OnNotificationReceivedAsync(NotificationEventArgs args)
		{
			this.GraphLogger.CorrelationId = args.ScenarioId;
			if (args.ResourceData is Call call)
			{
				botCallId = call.Id; botTenantId = args.TenantId; generalId = args.ScenarioId;
				if (args.ChangeType == ChangeType.Updated && call.State == CallState.Established)
				{
					await this.BotAnswerIncomingCallAsync(call.Id, args.TenantId, args.ScenarioId).ConfigureAwait(false);
				}
			}
			
		}

		private async Task BotAnswerIncomingCallAsync(string callId, string tenantId, Guid scenarioId)
		{

			Task answerTask = Task.Run(async () =>
								await this.graphServiceClient.Communications.Calls[callId].Answer(
									callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
									mediaConfig: new ServiceHostedMediaConfig
									{
										PreFetchMedia = new List<MediaInfo>()
										{
											//new MediaInfo()
											//{
											//	Uri = new Uri(options.BotBaseUrl, "audio/speech.wav").ToString(),
											//	ResourceId = Guid.NewGuid().ToString(),
											//},
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/test.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											}
										}
									},
									acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
								 );

			Task welcomeAnswer = await answerTask.ContinueWith(async (antecedent) =>
			{

				if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
				{
					await Task.Delay(5000);
					await graphServiceClient.Communications.Calls[callId].PlayPrompt(
						prompts: new List<Microsoft.Graph.Prompt>()
						{
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/test.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							}
						})
						.Request()
						.PostAsync();
				}
			});

			//await welcomeAnswer.ContinueWith(async (antecedent) =>
			//{
			//	if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
			//	{
			//		var recordOperation = await RecordAudio();
			//		var recording = new FileInfo(recordOperation.RecordingLocation);
			//		string storeLocation = Path.Combine(options.BotBaseUrl.ToString(), "audio", $"{new Guid()}.wav").ToString();
			//		recording.CopyTo(storeLocation, true);
			//		// TODO: Save it
			//		//recordOperation.RecordingLocation; 

			//		// TODO: Analyse recording
			//		//recordOperation.RecordingLocation; 

			//		// TODO: Get response with QnA
			//		//recordOperation.RecordingLocation; 

			//		// TODO: Play response prompt
			//		//recordOperation.RecordingLocation; 
			//	}
			//});

			// End Call - We could use it in case of caller does not responde or enter inlavild response
			//await this.graphServiceClient.Communications.Calls[callId].Request().DeleteAsync();

			// How to record response
			// https://docs.microsoft.com/en-us/graph/api/call-record?view=graph-rest-1.0&tabs=csharp
		}

		private async Task BotMenuAsync(string callId, string tenantId, Guid scenarioId)
		{
			Task answerTask = Task.Run(async () =>
								await this.graphServiceClient.Communications.Calls[callId].Answer(
									callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
									mediaConfig: new ServiceHostedMediaConfig
									{
										PreFetchMedia = new List<MediaInfo>()
										{
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/welcomeMessage.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											},
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/menu1.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											}
										}
									},
									acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
								 );

			Task welcomeAnswer = await answerTask.ContinueWith(async (antecedent) =>
			{

				if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
				{
					await Task.Delay(5000);
					await graphServiceClient.Communications.Calls[callId].PlayPrompt(
						prompts: new List<Microsoft.Graph.Prompt>()
						{
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/welcomeMessage.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							},
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/menu1.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							}
						})
						.Request()
						.PostAsync();
				}
			});

			step = 1;
		}
		private async Task BotSubMenuAsync(string callId, string tenantId, Guid scenarioId)
		{

			Task answerTask = Task.Run(async () =>
								await this.graphServiceClient.Communications.Calls[callId].Answer(
									callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
									mediaConfig: new ServiceHostedMediaConfig
									{
										PreFetchMedia = new List<MediaInfo>()
										{
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/submenu.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											}
										}
									},
									acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
								 );

			Task welcomeAnswer = await answerTask.ContinueWith(async (antecedent) =>
			{

				if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
				{
					await Task.Delay(5000);
					await graphServiceClient.Communications.Calls[callId].PlayPrompt(
						prompts: new List<Microsoft.Graph.Prompt>()
						{
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/submenu.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							}
						})
						.Request()
						.PostAsync();
				}
			});
			step = 2;
		}
		private async Task PrimeraOpcionAsync(string callId, string tenantId, Guid scenarioId)
		{

			Task answerTask = Task.Run(async () =>
								await this.graphServiceClient.Communications.Calls[callId].Answer(
									callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
									mediaConfig: new ServiceHostedMediaConfig
									{
										PreFetchMedia = new List<MediaInfo>()
										{
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/notImplemented.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											}
										}
									},
									acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
								 );

			Task welcomeAnswer = await answerTask.ContinueWith(async (antecedent) =>
			{

				if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
				{
					await Task.Delay(5000);
					await graphServiceClient.Communications.Calls[callId].PlayPrompt(
						prompts: new List<Microsoft.Graph.Prompt>()
						{
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/notImplemented.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							}
						})
						.Request()
						.PostAsync();
				}
			});
		}
		private async Task SegundaOpcionAsync(string callId, string tenantId, Guid scenarioId)
		{

			Task answerTask = Task.Run(async () =>
								await this.graphServiceClient.Communications.Calls[callId].Answer(
									callbackUri: new Uri(options.BotBaseUrl, "callback").ToString(),
									mediaConfig: new ServiceHostedMediaConfig
									{
										PreFetchMedia = new List<MediaInfo>()
										{
											new MediaInfo()
											{
												Uri = new Uri(options.BotBaseUrl, "audio/preguntas.wav").ToString(),
												ResourceId = Guid.NewGuid().ToString(),
											}
										}
									},
									acceptedModalities: new List<Modality> { Modality.Audio }).Request().PostAsync()
								 );

			Task welcomeAnswer = await answerTask.ContinueWith(async (antecedent) =>
			{

				if (antecedent.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
				{
					await Task.Delay(5000);
					await graphServiceClient.Communications.Calls[callId].PlayPrompt(
						prompts: new List<Microsoft.Graph.Prompt>()
						{
							new MediaPrompt
							{
								MediaInfo = new MediaInfo
								{
									Uri = new Uri(options.BotBaseUrl, "audio/preguntas.wav").ToString(),
									ResourceId = Guid.NewGuid().ToString(),
								}
							}
						})
						.Request()
						.PostAsync();
				}
			});

		}


		/// <summary>
		/// Record a piece of audio during the code
		/// </summary>
		/// <returns>Recording context</returns>
		private async Task<RecordOperation> RecordAudio(int maxRecordDurationInSeconds = 10, int initialSilenceTimeoutInSeconds = 5, int maxSilenceTimeoutInSeconds = 2)
		{
			var bargeInAllowed = true;

			var clientContext = "d45324c1-fcb5-430a-902c-f20af696537c";

			var prompts = new List<Prompt>()
			{
				new MediaPrompt
				{
					MediaInfo = new MediaInfo
					{
						Uri = "https://cdn.contoso.com/beep.wav",
						ResourceId = "1D6DE2D4-CD51-4309-8DAA-70768651088E"
					}
				}
			};

			var playBeep = true;

			var stopTones = new List<string>()
			{
				"#",
				"1",
				"*"
			};

			return await graphServiceClient.Communications.Calls["{call-id}"]
				.RecordResponse(prompts, bargeInAllowed, initialSilenceTimeoutInSeconds, maxSilenceTimeoutInSeconds, maxRecordDurationInSeconds, playBeep, stopTones, clientContext)
				.Request()
				.PostAsync();
		}
	}
}

