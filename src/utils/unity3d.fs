// Modules and Type Extensions that needed to be added for the Compiler Service to 
// run on the Unity .Net 3.5 Full Base Class Framework Profile

namespace Internal


open System
open System.Text
open System.Runtime.InteropServices

[<AutoOpen>]
module Extensions =
    type  Lazy<'T> with
        member self.Force() = self.Value


    type StringBuilder with
        /// Convenience method for sb.Length <- 0
        member self.Clear() =
            self.Length <- 0
            self


module Lazy = 
    let force (x: Lazy<'T>) = x.Force()


// Cribbed from  http://referencesource.microsoft.com/#mscorlib/system/weakreferenceoft.cs,c575dbe300e57438
// TODO finish implementing members
type WeakReference<'T when 'T : not struct>(target:'T, ?trackResurrection:bool) =

    let mutable _target :'T = target

    member self.SetTarget(target:'T) =
        _target <- target

    member self.Target with get() = _target
        

    member self.TryGetTarget( [<Out>] target:byref<'T>) =
            let o = self.Target
            target <- o
            not (obj.ReferenceEquals(o,null))

    new(target:'T) = WeakReference<'T>(target,false)


