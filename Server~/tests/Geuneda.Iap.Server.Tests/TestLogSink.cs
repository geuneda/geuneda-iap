using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Geuneda.Iap.Server.Tests
{
	/// <summary>Warning 이상 로그를 모읍니다(운영에서 Warning 이상만 수집할 때 남는 것).</summary>
	internal sealed class TestLogSink : ILoggerProvider
	{
		public ConcurrentQueue<string> Warnings { get; } = new ConcurrentQueue<string>();

		public ILogger CreateLogger(string categoryName)
		{
			return new SinkLogger(this);
		}

		public bool Contains(params string[] parts)
		{
			return Warnings.Any(message => parts.All(part => message.Contains(part, StringComparison.Ordinal)));
		}

		public void Dispose()
		{
		}

		private sealed class SinkLogger : ILogger
		{
			private readonly TestLogSink _sink;

			public SinkLogger(TestLogSink sink)
			{
				_sink = sink;
			}

			public IDisposable BeginScope<TState>(TState state) where TState : notnull
			{
				return null;
			}

			public bool IsEnabled(LogLevel logLevel)
			{
				return logLevel >= LogLevel.Warning;
			}

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
			{
				if (IsEnabled(logLevel))
				{
					_sink.Warnings.Enqueue(formatter(state, exception));
				}
			}
		}
	}
}
