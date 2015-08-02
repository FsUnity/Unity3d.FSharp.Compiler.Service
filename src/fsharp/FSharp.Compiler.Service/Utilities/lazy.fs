


type  Lazy<'T> with
    member self.Force() = self.Value


module Lazy = 
    let force (x: Lazy<'T>) = x.Force()

