/*
File: UserPresenceItem.cs

What this does:
- Purpose: View-model item representing a single user in the People/roster list with live-updating presence.
- How: Implements INotifyPropertyChanged to keep the UI in sync when properties change.
- Where used: Bound to the right-side "People" CollectionView in ChatPage; colors/labels react to Status.
*/

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chatter.Client.Models
{
    public class UserPresenceItem : INotifyPropertyChanged
    {
        private string _name;
        private PresenceStatus _status;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public PresenceStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsOnline));
                }
            }
        }

        // convenience for legacy bindings
        public bool IsOnline =>
            _status == PresenceStatus.Online ||
            _status == PresenceStatus.Away ||
            _status == PresenceStatus.Busy;

        public UserPresenceItem(string name, PresenceStatus status = PresenceStatus.Offline)
        {
            _name = name;
            _status = status;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
