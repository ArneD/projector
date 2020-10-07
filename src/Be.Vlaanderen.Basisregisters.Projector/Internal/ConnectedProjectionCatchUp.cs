namespace Be.Vlaanderen.Basisregisters.Projector.Internal
{
    using System;
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
    using SqlStreamStore;
    using SqlStreamStore.Streams;

    internal class ConnectedProjectionCatchUp<TContext> where TContext : RunnerDbContext<TContext>
    {
        private readonly IConnectedProjection<TContext> _projection;
        private readonly IConnectedProjectionsCommandBus _commandBus;
        private readonly ILogger _logger;
        private readonly IReadonlyStreamStore _streamStore;

        public int CatchUpPageSize { get; set; } = 1000;

        public ConnectedProjectionCatchUp(
            IConnectedProjection<TContext> projection,
            IReadonlyStreamStore streamStore,
            IConnectedProjectionsCommandBus commandBus,
            ILogger logger)
        {
            _projection = projection ?? throw new ArgumentNullException(nameof(projection));
            _streamStore = streamStore ?? throw new ArgumentNullException(nameof(streamStore));
            _commandBus = commandBus ?? throw new ArgumentNullException(nameof(commandBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CatchUpAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => CatchUpStopped(CatchUpStopReason.Aborted));

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                _logger.LogDebug(
                    "Started catch up with paging (CatchUpPageSize: {CatchUpPageSize})",
                    CatchUpPageSize);

                long? position;
                using (var context = _projection.ContextFactory())
                    position = await context.Value.GetRunnerPositionAsync(_projection.Name, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                _logger.LogInformation(
                    "Start catch up {RunnerName} at {Position}",
                    _projection.Name,
                    position);

                var page = await ReadPages(_streamStore, position, cancellationToken);

                var continueProcessing = cancellationToken.IsCancellationRequested == false;
                while (continueProcessing)
                {
                    _logger.LogDebug(
                        "Processing page of {PageSize} starting at POS {FromPosition}",
                        page.Messages.Length,
                        page.FromPosition);

                    await _projection.ConnectedProjectionMessageHandler.HandleAsync(page.Messages, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                        return;

                    if (page.IsEnd)
                        continueProcessing = false;
                    else
                        page = await page.ReadNext(cancellationToken);
                }

                CatchUpStopped(CatchUpStopReason.Finished);
            }
            catch (TaskCanceledException) { }
            catch (ConnectedProjectionMessageHandlingException exception)
            {
                _logger.LogError(
                    exception.InnerException,
                    "{RunnerName} catching up failed because an exception was thrown when handling the message at {Position}.",
                    exception.RunnerName,
                    exception.RunnerPosition);
                CatchUpStopped(CatchUpStopReason.Error);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "{RunnerName} catching up failed because an exception was thrown",
                    _projection.Name);
                CatchUpStopped(CatchUpStopReason.Error);
            }
        }

        private void CatchUpStopped(CatchUpStopReason reason)
        {
            var message = "Stopping catch up {RunnerName}: {Reason}";
            if (reason == CatchUpStopReason.Error)
                _logger.LogWarning(message, _projection.Name, reason);
            else
                _logger.LogInformation(message, _projection.Name, reason);

            _commandBus.Queue(new RemoveStoppedCatchUp(_projection.Name));
            if (CatchUpStopReason.Finished == reason)
                _commandBus.Queue(new Subscribe(_projection.Name));
        }

        private async Task<ReadAllPage> ReadPages(
            IReadonlyStreamStore streamStore,
            long? position,
            CancellationToken cancellationToken)
        {
            return await streamStore.ReadAllForwards(
                position + 1 ?? Position.Start,
                CatchUpPageSize,
                prefetchJsonData: true,
                cancellationToken);
        }
    }
}
