namespace Rdmp.Dicom.Cache.Pipeline.Ordering
{
    class Image
    {
        public string SOPInstanceUID { get; }
        
        public bool IsFilled { get; private set; }
        public bool IsRequested { get; private set; }

        public Image(string sopInstance)
        {
            IsFilled = false;
            IsRequested = false;
            SOPInstanceUID = sopInstance;
        }

        public void Fill()
        {
            IsFilled = true;
        }

        public void Request()
        {
            IsRequested = true;
        }
    }
}
