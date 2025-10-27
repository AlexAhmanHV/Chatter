/*
File: PresenceStatus.cs

What this does:
- Purpose: Defines the simple presence states a user can have in the client.
- Values:
  - Offline: Not available / not shown online.
  - Online : Active/available.
  - Away   : Temporarily idle.
  - Busy   : Do-not-disturb.
- Where used: Bound in the UI (status picker) and tracked in ChatViewModel for roster sorting and indicators.

Worth to mention: There is nothing automatic with this. It does not affect anything.
*/

namespace Chatter.Client.Models
{
    public enum PresenceStatus
    {
        Offline = 0,
        Online  = 1,
        Away    = 2,
        Busy    = 3
    }
}
