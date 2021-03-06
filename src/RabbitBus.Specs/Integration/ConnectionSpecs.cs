﻿using System;
using System.Threading;
using Machine.Specifications;
using Moq;
using RabbitBus.Specs.Infrastructure;
using RabbitBus.Specs.TestTypes;
using RabbitMQ.Client;
using It = Machine.Specifications.It;

namespace RabbitBus.Specs.Integration
{
	[Integration]
	[Subject("Connection unavailable")]
	public class when__the_broker_is_unavailable
	{
		static Bus _bus;
		static bool _connectionEstablished;
		static RabbitService _service;
		static bool _connectionFailed;

		Establish context = () =>
			{
				_service = new RabbitService();
				_service.Stop();

				_bus = new BusBuilder().Configure(ctx => ctx.WithLogger(new ConsoleLogger())).Build();
				_bus.ConnectionEstablished += (sender, e) => { _connectionEstablished = true; };
				_bus.ConnectionFailed += (sender, e) => { _connectionFailed = true; };
				new Thread(() => _bus.Connect()).Start();
				Wait.Until(() => _connectionFailed);
			};


		Cleanup after = () =>
			{
				_bus.Close();
				_service.Start();
			};

		Because of = () => new Action(() => _service.Start()).BlockUntil(() => _connectionEstablished)();

		It should_connect_when_the_broker_becomes_available = () => _connectionEstablished.ShouldBeTrue();
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_the_connection_is_restarted
	{
		const string SpecId = "2BD27137-9AE3-4342-B4C3-9DEC20D203DF";
		static RabbitExchange _rabbitExchange;
		static TestMessage _actualMessage = new TestMessage("wrong");
		static Bus _bus;
		static bool _connectionRestablished;

		Establish context = () =>
			{
				_bus = new BusBuilder()
					.Configure(ctx =>
					           ctx.WithLogger(new ConsoleLogger())
					           	.Consume<TestMessage>().WithExchange(SpecId).WithQueue(SpecId)).Build();
				_bus.Connect();
				_bus.Subscribe<TestMessage>(ctx => { _actualMessage = ctx.Message; });
				new RabbitService().Restart();
				_bus.ConnectionEstablished += (b, e) => { _connectionRestablished = true; };
				Wait.Until(() => _connectionRestablished);
				_rabbitExchange = new RabbitExchange("localhost", SpecId, ExchangeType.Direct);
			};

		Cleanup after = () =>
			{
				_bus.Close();
				_rabbitExchange.Close();
			};

		Because of = () =>
			new Action(() => _rabbitExchange.Publish(new TestMessage("test"))).BlockUntil(() => _actualMessage.Text != "wrong")();

		It should_resume_prior_subscriptions = () => _actualMessage.Text.ShouldEqual("test");
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_publishing_an_event_when_the_connection_is_down_with_default_queue_strategy
	{
		const string SpecId = "FD8A635E-0287-4D9E-8506-B0AC56A02641";
		static Bus _bus;
		static TestMessage _actualMessage = new TestMessage("default");
		static RabbitQueue _rabbitQueue;

		Establish context = () =>
			{
				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Close();

				_bus = new BusBuilder()
					.Configure(ctx => ctx.WithLogger(new ConsoleLogger())
					                  	.WithConnectionUnavailableQueueStrategy(new MemoryQueueStrategy())
					                  	.Publish<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable())).Build();
				_bus.Connect();

				new RabbitService().Stop();
				_bus.Publish(new TestMessage("test"));
				new RabbitService().Start();
				bool connectionRestablished = false;
				_bus.ConnectionEstablished += (b, e) => { connectionRestablished = true; };
				Wait.Until(() => connectionRestablished);

				_rabbitQueue = new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false);
			};

		Cleanup after = () =>
			{
				_bus.Close();
				_rabbitQueue.Delete().Close();
			};

		Because of =
			() =>
			new Action(() => _actualMessage = _rabbitQueue.GetMessage<TestMessage>()).BlockUntil(
				() => _actualMessage.Text != "default")();

		It should_publish_the_event_when_the_connection_is_restored = () => _actualMessage.Text.ShouldEqual("test");
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_publishing_an_event_when_the_connection_is_down_without_a_queuing_strategy_configured
	{
		const string SpecId = "8D43FE19-35FF-4B70-A9E9-65BF19725639";
		static Bus _bus;
		static readonly TestMessage _actualMessage = new TestMessage("default");
		static Exception _exception;

		Establish context = () =>
			{
				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Close();

				_bus = new BusBuilder().Configure(ctx =>
					{
						ctx.WithLogger(new ConsoleLogger());
						ctx.Publish<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable());
					}).Build();
				_bus.Connect();

				new RabbitService().Stop();
			};

		Cleanup after = () =>
			{
				Console.WriteLine("Cleaning up");
				new RabbitService().Start();
				_bus.Close();
				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Delete().Close();
			};

		Because of = () => _exception = Catch.Exception(() => _bus.Publish(new TestMessage("test")));

		It should_throw_a_connection_unabailable_exception = () => _exception.ShouldBeOfType<ConnectionUnavailableException>();
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_publishing_and_subscribing_when_the_connection_is_down
	{
		const string SpecId = "BE95718E-A106-4DE0-A006-5B1F45A80389";
		static Bus _publisher;
		static TestMessage _actualMessage = new TestMessage("default");
		static Bus _subscriber;

		Establish context = () =>
			{
				_publisher = new BusBuilder()
					.Configure(ctx => ctx.WithLogger(new ConsoleLogger())
					                  	.WithConnectionUnavailableQueueStrategy(new MemoryQueueStrategy())
					                  	.Publish<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable())).Build();
				_publisher.Connect();

				_subscriber = new BusBuilder()
					.Configure(ctx => ctx
					                  	.WithLogger(new ConsoleLogger())
					                  	.Consume<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable())
					                  	.WithQueue(SpecId, cfg => cfg.Not.AutoDelete().Durable())).Build();
				_subscriber.Connect();
				_subscriber.Subscribe<TestMessage>(ctx => _actualMessage = ctx.Message);

				new RabbitService().Stop();
				_publisher.Publish(new TestMessage("test"));
				new RabbitService().Start();
				bool connectionRestablished = false;
				_publisher.ConnectionEstablished += (b, e) => { connectionRestablished = true; };
				Wait.Until(() => connectionRestablished);

				Wait.Until(() => _actualMessage != null);
			};

		Cleanup after = () =>
			{
				_publisher.Close();
				_subscriber.Close();
				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Delete().Close();
			};

		It should_restore_functionality_to_both_the_publisher_and_the_subscriber =
			() => _actualMessage.Text.ShouldEqual("test");
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_configuring_a_reconnection_attempt_interval
	{
		const string SpecId = "47E8BFC3-2317-44D6-A5CE-48C81E35C02F";
		static Mock<ITimeProvider> _mockTimeProvider;
		static Bus _bus;
		static bool _connectionRestablished;

		Establish context = () =>
			{
				_mockTimeProvider = new Mock<ITimeProvider>();

				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Close();

				TimeProvider.SetCurrent(_mockTimeProvider.Object);

				_bus = new BusBuilder()
					.Configure(ctx => ctx
					                  	.WithLogger(new ConsoleLogger())
					                  	.WithReconnectionAttemptInterval(TimeSpan.FromSeconds(5))
					                  	.WithConnectionUnavailableQueueStrategy(new MemoryQueueStrategy())
					                  	.Publish<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable())).Build();
				_bus.Connect();

				_bus.ConnectionEstablished += (b, e) => { _connectionRestablished = true; };
			};

		Cleanup after = () =>
			{
				new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Delete().Close();
				_bus.Close();
			};

		Because of = () => new Action(() => new RabbitService().Restart()).BlockUntil(() => _connectionRestablished)();

		It should_reconnect_using_the_configured_timeout =
			() => _mockTimeProvider.Verify(x => x.Sleep(TimeSpan.FromSeconds(5)));
	}

	[Integration]
	[Subject("Connection interruption")]
	public class when_configuring_a_reconnection_attempt_timeout_value
	{
		const string SpecId = "47E8BFC3-2317-44D6-A5CE-48C81E35C02F";
		static Mock<ITimeProvider> _mockTimeProvider;
		static Bus _bus;
		static bool _connectionTimeout;

		Establish context = () =>
		{
			_mockTimeProvider = new Mock<ITimeProvider>();

			new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Close();

			TimeProvider.SetCurrent(_mockTimeProvider.Object);

			_bus = new BusBuilder()
				.Configure(ctx => ctx
														.WithLogger(new ConsoleLogger())
														.WithReconnectionAttemptTimeout(TimeSpan.FromSeconds(5))
														.WithConnectionUnavailableQueueStrategy(new MemoryQueueStrategy())
														.Publish<TestMessage>().WithExchange(SpecId, cfg => cfg.Not.AutoDelete().Durable())).Build();
			_bus.Connect();

			_bus.ConnectionTimeout += (b, e) => { _connectionTimeout = true; };
		};

		Cleanup after = () =>
		{
			new RabbitService().Start();
			new RabbitQueue("localhost", SpecId, ExchangeType.Direct, SpecId, true, false, true, false).Delete().Close();
			_bus.Close();
			
		};

		Because of = () => new Action(() => new RabbitService().Stop()).BlockUntil(() => _connectionTimeout)();

		It should_reconnect_using_the_configured_timeout = () => _connectionTimeout.ShouldBeTrue();
	}
}