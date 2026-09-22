namespace Sample.FSharp

open Scry
open Scry.Generated

module Commands =

    // begin-snippet: fsharpCommand
    /// A command from F#: the generated class, its properties set in the constructor call, sent through
    /// the generated facade. The outcome is a Task like any other — Completed where the server decided
    /// it within its sync window, Pending with its Completion to await where it did not.
    let rename (query: ScryQuery) (id: int) (name: string) =
        query.Commands.RenameEmployee(RenameEmployee(Id = id, Name = name))

    /// A command that answers with a result: the typed outcome's Value is the class the handler
    /// answered with, read only once EnsureCompleted has said the command did complete.
    let hire (query: ScryQuery) (name: string) (departmentId: int) =
        task {
            let! outcome =
                query.Commands.CreateEmployee(CreateEmployee(Name = name, DepartmentId = departmentId, Status = Status.FullTime))

            return outcome.EnsureCompleted().Value.Id
        }
    // end-snippet
