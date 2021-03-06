﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Exceptionless.Api.Tests.Utility;
using Exceptionless.Core.Billing;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Plugins.EventParser;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Repositories;
using Exceptionless.Models;
using Exceptionless.Tests.Utility;
using Nest;
using Xunit;
using Xunit.Extensions;

namespace Exceptionless.Api.Tests.Pipeline {
    public class EventPipelineTests : IDisposable {
        private readonly IOrganizationRepository _organizationRepository = IoC.GetInstance<IOrganizationRepository>();
        private readonly IProjectRepository _projectRepository = IoC.GetInstance<IProjectRepository>();
        private readonly ITokenRepository _tokenRepository = IoC.GetInstance<ITokenRepository>();
        private readonly IStackRepository _stackRepository = IoC.GetInstance<IStackRepository>();
        private readonly IEventRepository _eventRepository = IoC.GetInstance<IEventRepository>();
        private readonly UserRepository _userRepository = IoC.GetInstance<UserRepository>();

        public EventPipelineTests() {
            RemoveData(true);
            CreateData();
        }

        [Fact]
        public void NoFutureEvents() {
            var localTime = DateTime.Now;
            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: localTime.AddMinutes(10));

            var pipeline = IoC.GetInstance<EventPipeline>();
            Assert.DoesNotThrow(() => pipeline.Run(ev));

            var client = IoC.GetInstance<IElasticClient>();
            client.Refresh();
            ev = _eventRepository.GetById(ev.Id);
            Assert.NotNull(ev);
            Assert.True(ev.Date < localTime.AddMinutes(10));
            Assert.True(ev.Date - localTime < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public void CanIndexExtendedData() {
            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, generateData: false, occurrenceDate: DateTime.Now);
            ev.SetProperty("First Name", "Eric");
            ev.SetProperty("IsVerified", true);
            ev.SetProperty("IsVerified1", true.ToString());
            ev.SetProperty("Age", Int32.MaxValue);
            ev.SetProperty("Age1", Int32.MaxValue.ToString(CultureInfo.InvariantCulture));
            ev.SetProperty("AgeDec", Decimal.MaxValue);
            ev.SetProperty("AgeDec1", Decimal.MaxValue.ToString(CultureInfo.InvariantCulture));
            ev.SetProperty("AgeDbl", Double.MaxValue);
            ev.SetProperty("AgeDbl1", Double.MaxValue.ToString("r", CultureInfo.InvariantCulture));
            ev.SetProperty(" Birthday ", DateTime.MinValue);
            ev.SetProperty("BirthdayWithOffset", DateTimeOffset.MinValue);
            ev.SetProperty("@excluded", DateTime.MinValue);
            ev.AddObject(new { State = "Texas" }, "Address");

            var pipeline = IoC.GetInstance<EventPipeline>();
            Assert.DoesNotThrow(() => pipeline.Run(ev));
            Assert.Equal(11, ev.Idx.Count);
            Assert.True(ev.Idx.ContainsKey("first-name-s"));
            Assert.True(ev.Idx.ContainsKey("isverified-b"));
            Assert.True(ev.Idx.ContainsKey("isverified1-b"));
            Assert.True(ev.Idx.ContainsKey("age-n"));
            Assert.True(ev.Idx.ContainsKey("age1-n"));
            Assert.True(ev.Idx.ContainsKey("agedec-n"));
            Assert.True(ev.Idx.ContainsKey("agedec1-n"));
            Assert.True(ev.Idx.ContainsKey("agedbl-n"));
            Assert.True(ev.Idx.ContainsKey("agedbl1-n"));
            Assert.True(ev.Idx.ContainsKey("birthday-d"));
            Assert.True(ev.Idx.ContainsKey("birthdaywithoffset-d"));
        }

        [Fact]
        public void SyncStackTags() {
            const string Tag1 = "Tag One";
            const string Tag2 = "Tag Two";
            const string Tag2_Lowercase = "tag two";
            var client = IoC.GetInstance<IElasticClient>();

            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag1);

            var pipeline = IoC.GetInstance<EventPipeline>();
            Assert.DoesNotThrow(() => pipeline.Run(ev));

            client.Refresh();
            ev = _eventRepository.GetById(ev.Id);
            Assert.NotNull(ev);
            Assert.NotNull(ev.StackId);

            var stack = _stackRepository.GetById(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1 }, stack.Tags);

            ev = EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag2);

            Assert.DoesNotThrow(() => pipeline.Run(ev));
            stack = _stackRepository.GetById(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1, Tag2 }, stack.Tags);

            ev = EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag2_Lowercase);

            Assert.DoesNotThrow(() => pipeline.Run(ev));
            stack = _stackRepository.GetById(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1, Tag2 }, stack.Tags);
        }

       [Fact]
        public void EnsureSingleNewStack() {
            var pipeline = IoC.GetInstance<EventPipeline>();

            string source = Guid.NewGuid().ToString();
            var contexts = new List<EventContext> {
                new EventContext(new PersistentEvent { ProjectId = TestConstants.ProjectId, OrganizationId = TestConstants.OrganizationId, Message = "Test Sample", Source = source, Date = DateTime.UtcNow, Type = Event.KnownTypes.Log }),
                new EventContext(new PersistentEvent { ProjectId = TestConstants.ProjectId, OrganizationId = TestConstants.OrganizationId, Message = "Test Sample", Source = source, Date = DateTime.UtcNow, Type = Event.KnownTypes.Log}),
            };

            Assert.DoesNotThrow(() => pipeline.Run(contexts));
            Assert.True(contexts.All(c => c.Stack.Id == contexts.First().Stack.Id));
            Assert.Equal(1, contexts.Count(c => c.IsNew));
            Assert.Equal(1, contexts.Count(c => !c.IsNew));
            Assert.Equal(2, contexts.Count(c => !c.IsRegression));
        }

        [Fact]
        public void EnsureSingleRegression() {
            var pipeline = IoC.GetInstance<EventPipeline>();
            var client = IoC.GetInstance<IElasticClient>();

            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow);
            var context = new EventContext(ev);
            Assert.DoesNotThrow(() => pipeline.Run(context));
            Assert.True(context.IsProcessed);
            Assert.False(context.IsRegression);

            client.Refresh();
            ev = _eventRepository.GetById(ev.Id);
            Assert.NotNull(ev);

            var stack = _stackRepository.GetById(ev.StackId);
            stack.DateFixed = DateTime.UtcNow;
            stack.IsRegressed = false;
            _stackRepository.Save(stack, true);

            var contexts = new List<EventContext> {
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1))),
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1)))
            };

            Assert.DoesNotThrow(() => pipeline.Run(contexts));
            Assert.Equal(1, contexts.Count(c => c.IsRegression));
            Assert.Equal(1, contexts.Count(c => !c.IsRegression));

            contexts = new List<EventContext> {
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1))),
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1)))
            };

            Assert.DoesNotThrow(() => pipeline.Run(contexts));
            Assert.Equal(2, contexts.Count(c => !c.IsRegression));
        }

        [Theory]
        [PropertyData("Events")]
        public void ProcessEvents(string errorFilePath) {
            var parserPluginManager = IoC.GetInstance<EventParserPluginManager>();
            var events = parserPluginManager.ParseEvents(File.ReadAllText(errorFilePath), 2, "exceptionless/2.0.0.0");
            Assert.NotNull(events);
            Assert.True(events.Count > 0);

            var pipeline = IoC.GetInstance<EventPipeline>();
            foreach (var ev in events) {
                ev.ProjectId = TestConstants.ProjectId;
                ev.OrganizationId = TestConstants.OrganizationId;

                Assert.DoesNotThrow(() => pipeline.Run(ev));
            }
        }

        public static IEnumerable<object[]> Events {
            get {
                var result = new List<object[]>();
                foreach (var file in Directory.GetFiles(@"..\..\ErrorData\", "*.expected.json", SearchOption.AllDirectories))
                    result.Add(new object[] { file });

                return result.ToArray();
            }
        }

        private void CreateData() {
            foreach (Organization organization in OrganizationData.GenerateSampleOrganizations()) {
                if (organization.Id == TestConstants.OrganizationId3)
                    BillingManager.ApplyBillingPlan(organization, BillingManager.FreePlan, UserData.GenerateSampleUser());
                else
                    BillingManager.ApplyBillingPlan(organization, BillingManager.SmallPlan, UserData.GenerateSampleUser());

                organization.StripeCustomerId = Guid.NewGuid().ToString("N");
                organization.CardLast4 = "1234";
                organization.SubscribeDate = DateTime.Now;

                if (organization.IsSuspended) {
                    organization.SuspendedByUserId = TestConstants.UserId;
                    organization.SuspensionCode = SuspensionCode.Billing;
                    organization.SuspensionDate = DateTime.Now;
                }

                _organizationRepository.Add(organization);
            }

            foreach (Project project in ProjectData.GenerateSampleProjects()) {
                var organization = _organizationRepository.GetById(project.OrganizationId);
                _organizationRepository.Save(organization);

                _projectRepository.Add(project);
            }

            foreach (User user in UserData.GenerateSampleUsers()) {
                if (user.Id == TestConstants.UserId) {
                    user.OrganizationIds.Add(TestConstants.OrganizationId2);
                    user.OrganizationIds.Add(TestConstants.OrganizationId3);
                }

                if (!user.IsEmailAddressVerified) {
                    user.CreateVerifyEmailAddressToken();
                }
                _userRepository.Add(user);
            }
        }

        private void RemoveData(bool removeUserAndProjectAndOrganizationData = false) {
            _eventRepository.RemoveAll();
            _stackRepository.RemoveAll();

            if (!removeUserAndProjectAndOrganizationData)
                return;

            _tokenRepository.RemoveAll();
            _userRepository.RemoveAll();
            _projectRepository.RemoveAll();
            _organizationRepository.RemoveAll();
        }

        public void Dispose() {
            RemoveData();
        }
    }
}