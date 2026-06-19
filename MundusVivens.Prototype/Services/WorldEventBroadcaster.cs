using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using MundusVivens.Prototype.Protos;

namespace MundusVivens.Prototype.Services
{
    public interface IWorldEventBroadcaster
    {
        event Action<WorldEvent>? OnWorldEvent;
        Task SubscribeAsync(string subscriberId, IServerStreamWriter<WorldEvent> responseStream, CancellationToken cancellationToken);
        Task BroadcastAsync(WorldEvent worldEvent);
    }

    public class WorldEventBroadcaster : IWorldEventBroadcaster
    {
        public event Action<WorldEvent>? OnWorldEvent;
        private readonly ILogger<WorldEventBroadcaster> _logger;
        private readonly ConcurrentDictionary<string, IServerStreamWriter<WorldEvent>> _subscribers = new();

        public WorldEventBroadcaster(ILogger<WorldEventBroadcaster> logger)
        {
            _logger = logger;
        }

        public async Task SubscribeAsync(string subscriberId, IServerStreamWriter<WorldEvent> responseStream, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Broadcaster] Subscriber connected: {subscriberId}");

            // 동일한 ID가 이미 연결되어 있다면 기존 것 정리 후 덮어씀
            if (_subscribers.TryRemove(subscriberId, out _))
            {
                _logger.LogWarning($"[Broadcaster] Cleaned up duplicate subscriber: {subscriberId}");
            }

            _subscribers.TryAdd(subscriberId, responseStream);

            try
            {
                // 첫 연결 확인용 웰컴 이벤트 송신
                var welcomeEvent = new WorldEvent
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Tick = new TickEvent { TickNumber = 0 } // 0번 틱으로 초기 통신 확인
                };
                await responseStream.WriteAsync(welcomeEvent);

                // 클라이언트가 연결을 끊을 때까지 대기
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation($"[Broadcaster] Subscriber disconnected normally: {subscriberId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Broadcaster] Subscriber error ({subscriberId}): {ex.Message}");
            }
            finally
            {
                _subscribers.TryRemove(subscriberId, out _);
                _logger.LogInformation($"[Broadcaster] Subscriber cleaned up: {subscriberId}");
            }
        }

        public async Task BroadcastAsync(WorldEvent worldEvent)
        {
            try
            {
                OnWorldEvent?.Invoke(worldEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Broadcaster] SSE event publish failed: {ex.Message}");
            }

            if (_subscribers.IsEmpty)
            {
                return;
            }

            _logger.LogInformation($"[Broadcaster] Broadcasting event: {worldEvent.EventCase} (Subscribers: {_subscribers.Count})");

            var tasks = new System.Collections.Generic.List<Task>();

            foreach (var kvp in _subscribers)
            {
                var id = kvp.Key;
                var stream = kvp.Value;

                // 각 구독자에게 개별 비동기로 송신하여 한 쪽의 병목이 전체 브로드캐스팅을 막지 않도록 처리
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await stream.WriteAsync(worldEvent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"[Broadcaster] Failed to send event to {id}, removing subscriber. Error: {ex.Message}");
                        _subscribers.TryRemove(id, out _);
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
    }
}
