module Step2.Program

open Step2.Domain
open Step2.Infrastructure

let eventStore : EventStore<Event> = EventStore.initialize()

let sold () =
  eventStore.Get()
  |> List.fold Projections.soldIcecreams.Update Projections.soldIcecreams.Init

eventStore.Append [IcecreamSold Vanilla]
eventStore.Append [IcecreamSold Strawberry ; Flavour_empty Strawberry]
eventStore.Append [IcecreamSold Vanilla]
eventStore.Append [IcecreamSold Vanilla]

sold()

eventStore.Append [IcecreamSold Strawberry]
eventStore.Append [IcecreamSold Vanilla]

sold()



