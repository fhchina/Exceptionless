﻿#region Copyright 2014 Exceptionless

// This program is free software: you can redistribute it and/or modify it 
// under the terms of the GNU Affero General Public License as published 
// by the Free Software Foundation, either version 3 of the License, or 
// (at your option) any later version.
// 
//     http://www.gnu.org/licenses/agpl-3.0.html

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Repositories;
using NLog.Fluent;

namespace Exceptionless.Core.Pipeline {
    [Priority(30)]
    public class CheckForRegressionAction : EventPipelineActionBase {
        private readonly IStackRepository _stackRepository;
        private readonly IEventRepository _eventRepository;

        public CheckForRegressionAction(IStackRepository stackRepository, IEventRepository eventRepository) {
            _stackRepository = stackRepository;
            _eventRepository = eventRepository;
        }

        protected override bool ContinueOnError { get { return true; } }

        public override void ProcessBatch(ICollection<EventContext> contexts) {
            var stacks = contexts.Where(c => c.Stack != null && c.Stack.DateFixed.HasValue && c.Stack.DateFixed.Value < c.Event.Date.UtcDateTime).GroupBy(c => c.Event.StackId);
            foreach (var stackGroup in stacks) {
                var context = stackGroup.First();
                Log.Trace().Message("Marking stack and events as regression.").Write();
                _stackRepository.MarkAsRegressed(context.Stack.Id);
                _eventRepository.MarkAsRegressedByStack(context.Event.OrganizationId, context.Stack.Id);

                _stackRepository.InvalidateCache(context.Event.ProjectId, context.Event.StackId, context.SignatureHash);

                bool isFirstEvent = true;
                foreach (var ctx in stackGroup) {
                    ctx.Event.IsFixed = false;

                    // Only mark the first event context as regressed.
                    ctx.IsRegression = isFirstEvent;
                    isFirstEvent = false;
                }
            }
        }

        public override void Process(EventContext ctx) { }
    }
}