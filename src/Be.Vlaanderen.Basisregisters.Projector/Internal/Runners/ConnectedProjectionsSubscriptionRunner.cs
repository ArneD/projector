namespace Be.Vlaanderen.Basisregisters.Projector.Internal.Runners
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Commands;
    using Commands.CatchUp;
    using Commands.Subscription;
    using ConnectedProjections;
    using Exceptions;
    using Extensions;
    using Microsoft.Extensions.Logging;
    using ProjectionHandling.Runner;
    using SqlStreamStore.Streams;

    internal interface IConnectedProjectionsSubscriptionRunner
    {
        Task HandleSubscriptionCommand<TSubscriptionCommand>(TSubscriptionCommand command)
            where TSubscriptionCommand : SubscriptionCommand;
    }

    internal class ConnectedProjectionsSubscriptionRunner : IConnectedProjectionsSubscriptionRunner
    {
        private readonly Dictionary<ConnectedProjectionName, Func<StreamMessage, CancellationToken, Task>> _handlers;
        private readonly IRegisteredProjections _registeredProjections;
        private readonly IConnectedProjectionsStreamStoreSubscription _streamsStoreSubscription;
        private readonly IConnectedProjectionsCommandBus _commandBus;
        private readonly ILogger _logger;

        private long? _lastProcessedMessagePosition;

        public ConnectedProjectionsSubscriptionRunner(
            IRegisteredProjections registeredProjections,
            IConnectedProjectionsStreamStoreSubscription streamsStoreSubscription,
            IConnectedProjectionsCommandBus commandBus,
            ILoggerFactory loggerFactory)
        {
            _handlers = new Dictionary<ConnectedProjectionName, Func<StreamMessage, CancellationToken, Task>>();

            _registeredProjections = registeredProjections ?? throw new ArgumentNullException(nameof(registeredProjections));
            _registeredProjections.IsSubscribed = HasSubscription;

            _streamsStoreSubscription = streamsStoreSubscription ?? throw new ArgumentNullException(nameof(streamsStoreSubscription));
            _commandBus = commandBus ?? throw new ArgumentNullException(nameof(commandBus));
            _logger = loggerFactory?.CreateLogger<ConnectedProjectionsSubscriptionRunner>() ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        private bool HasSubscription(ConnectedProjectionName projectionName)
            => projectionName != null && _handlers.ContainsKey(projectionName);

        public async Task HandleSubscriptionCommand<TSubscriptionCommand>(TSubscriptionCommand command)
            where TSubscriptionCommand : SubscriptionCommand
        {
            _logger.LogTrace("Subscription: Handling {Command}", command);
            switch (command)
            {
                case ProcessStreamEvent processStreamEvent:
                    await Handle(processStreamEvent);
                    break;

                case Subscribe subscribe:
                    await Handle(subscribe);
                    break;

                case SubscribeAll _:
                    await SubscribeAll();
                    break;

                case Unsubscribe unsubscribe:
                    Handle(unsubscribe);
                    break;

                case UnsubscribeAll _:
                    UnsubscribeAll();
                    break;

                default:
                    _logger.LogError("No handler defined for {Command}", command);
                    break;
            }
        }

        private async Task StartStream()
        {
            if (_streamsStoreSubscription.StreamIsRunning)
                return;

            if (_handlers.Count > 0)
            {
                var staleSubscriptions = _handlers.Keys.ToReadOnlyList();
                _logger.LogInformation("Remove stale subscriptions before starting stream: {subscriptions}", staleSubscriptions.ToString(", "));
                _handlers.Clear();

                foreach (var name in staleSubscriptions)
                    _commandBus.Queue(new Start(name));
            }

            _lastProcessedMessagePosition = await _streamsStoreSubscription.Start();
        }

        private async Task Handle(Subscribe subscribe)
        {
            if (_streamsStoreSubscription.StreamIsRunning)
            {
                var projection = _registeredProjections
                    .GetProjection(subscribe?.ProjectionName)
                    ?.Instance;

                Subscribe(projection);
            }
            else
            {
                await StartStream();
                _commandBus.Queue(subscribe.Clone());
            }
        }

        private async Task SubscribeAll()
        {
            if (_streamsStoreSubscription.StreamIsRunning)
            {
                foreach (var projectionName in _registeredProjections.Names)
                    await Handle(new Subscribe(projectionName));
            }
            else
            {
                await StartStream();
                _commandBus.Queue<SubscribeAll>();
            }
        }

        private void Handle(Unsubscribe unsubscribe)
        {
            if (unsubscribe?.ProjectionName == null)
                return;

            _logger.LogInformation("Unsubscribing {Projection}", unsubscribe.ProjectionName);
            _handlers.Remove(unsubscribe.ProjectionName);
            if (_handlers.Count == 0)
                _lastProcessedMessagePosition = null;
        }

        private void UnsubscribeAll()
        {
            _logger.LogInformation("Unsubscribing {Projections}", _handlers.Keys.ToString(", "));
            _handlers.Clear();
            _lastProcessedMessagePosition = null;
        }

        private async Task Subscribe<TContext>(IConnectedProjection<TContext> projection)
            where TContext : RunnerDbContext<TContext>
        {
            if (projection == null || _registeredProjections.IsProjecting(projection.Name))
                return;

            long? projectionPosition;
            using (var context = projection.ContextFactory())
                projectionPosition = await context.Value.GetRunnerPositionAsync(projection.Name, CancellationToken.None);

            if ((projectionPosition ?? -1) >= (_lastProcessedMessagePosition ?? -1))
            {
                _logger.LogInformation(
                    "Subscribing {ProjectionName} at {ProjectionPosition} to AllStream at {StreamPosition}",
                    projection.Name,
                    projectionPosition,
                    _lastProcessedMessagePosition);

                _handlers.Add(
                    projection.Name,
                    async (message, token) => await projection.ConnectedProjectionMessageHandler.HandleAsync(message, token));
            }
            else
                _commandBus.Queue(new StartCatchUp(projection.Name));
        }

        private async Task Handle(ProcessStreamEvent processStreamEvent)
        {
            if (_handlers.Count == 0)
                return;

            _lastProcessedMessagePosition = processStreamEvent.Message.Position;

            _logger.LogTrace(
                "Handling message {MessageType} at {Position}",
                processStreamEvent.Message.Type,
                processStreamEvent.Message.Position);


            foreach (var projectionName in _handlers.Keys.ToReadOnlyList())
            {
                try
                {
                    await _handlers[projectionName](processStreamEvent.Message, processStreamEvent.CancellationToken);
                }
                catch (ConnectedProjectionMessageHandlingException messageHandlingException)
                {
                    var projectionInError = messageHandlingException.RunnerName;
                    _logger.LogError(
                        messageHandlingException.InnerException,
                        "Handle message Subscription {RunnerName} failed because an exception was thrown when handling the message at {Position}.",
                        projectionInError,
                        messageHandlingException.RunnerPosition);

                    _logger.LogWarning(
                        "Stopped faulty subscribed projection {Projection}",
                        projectionInError);

                    _handlers.Remove(projectionInError);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Unhandled exception in Handle:{Command}.", nameof(ProcessStreamEvent));

                    _logger.LogInformation(
                        "Removing faulty subscribed projection {Projection}",
                        projectionName);

                    _handlers.Remove(projectionName);
                }
            }
        }
    }
}
