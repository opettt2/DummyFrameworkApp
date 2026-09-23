using System;

namespace DummyFrameworkApp
{
    public partial class Default : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            lblTimestamp.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
