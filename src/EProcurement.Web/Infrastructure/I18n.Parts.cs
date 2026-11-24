namespace EProcurement.Web.Infrastructure;

public static partial class I18n
{
    /// <summary>Các phần từ điển mở rộng (mỗi phần một file I18n.En.ExtN.cs) – khai báo thêm ở đây khi tạo phần mới</summary>
    private static IEnumerable<Dictionary<string, string>> ExtParts()
    {
        yield return Ext1(); yield return Ext2(); yield return Ext3(); yield return Ext4(); yield return Ext5(); yield return Ext6();
    }
}
