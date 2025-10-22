// Models/UserPresenceItem.cs
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
                    OnPropertyChanged(nameof(IsOnline)); // keep bindings in sync
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
