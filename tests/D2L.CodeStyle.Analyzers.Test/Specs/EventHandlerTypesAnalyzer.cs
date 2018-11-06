
// analyzer: D2L.CodeStyle.Analyzers.ApiUsage.Events.EventHandlerTypesAnalyzer


namespace D2L.LP.Distributed.Events.Handlers {

	using System;

	[AttributeUsage( validOn: AttributeTargets.Class, AllowMultiple = false, Inherited = false )]
	public sealed class EventHandlerAttribute : Attribute {
		public EventHandlerAttribute( string id ) { }
	}
}

namespace Tests {

	using System;
	using D2L.LP.Distributed.Events.Handlers;

	[Event]
	public sealed class JumpEvent { }

	[EventHandler( "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA" )]
	public sealed class AttributedJumpEventHandler { }

	[EventHandler( "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB" )]
	public sealed class AttributedJumpOrgEventHandler { }
}
