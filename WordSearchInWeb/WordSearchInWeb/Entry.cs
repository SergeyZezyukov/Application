using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WordSearchInWeb
{
    public enum Status
    {
        Processing,
        Found,
        NotFound,
        Error
    }
    public enum State
    {
        Processing,
        Stop,
        Done
    }
    public class Entry
    {
        public string Url { get; set; }
        public Status Status { get; set; }
    }
}
