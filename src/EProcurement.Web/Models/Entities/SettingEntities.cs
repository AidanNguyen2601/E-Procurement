using System.ComponentModel.DataAnnotations;

namespace EProcurement.Web.Models.Entities;

/// <summary>Cấu hình hệ thống dạng khóa – giá trị (vd: giao diện mặc định)</summary>
public class AppSetting
{
    [Key, StringLength(100)] public string Key { get; set; } = string.Empty;
    [StringLength(4000)] public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
