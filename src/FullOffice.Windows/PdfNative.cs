using System.Runtime.InteropServices;

namespace FlowOfficeLab;

internal static class PdfNative
{
    const string Dll = "pdfium.dll";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_GetMatrix(IntPtr text,int index,out Matrix matrix);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_SetFlags(IntPtr annotation,int flags);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_GetCharOrigin(IntPtr text,int index,out double x,out double y);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern double FPDFText_GetFontSize(IntPtr text,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPage_RemoveAnnot(IntPtr page,int index);
    [StructLayout(LayoutKind.Sequential)] internal struct Matrix { public float a,b,c,d,e,f; }
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDFText_GetUnicode(IntPtr text,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern int FPDFText_GetBoundedText(IntPtr text,double left,double top,double right,double bottom,[Out] char[]? buffer,int count);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPage_CountObjects(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFPage_GetObject(IntPtr page,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetBounds(IntPtr obj,out float left,out float bottom,out float right,out float top);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPageObj_GetMatrix(IntPtr obj,out Matrix matrix);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFFormObj_CountObjects(IntPtr obj);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFFormObj_GetObject(IntPtr obj,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFImageObj_GetBitmap(IntPtr image);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetFormat(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetWidth(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetHeight(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFBookmark_GetCount(IntPtr bookmark);
    [StructLayout(LayoutKind.Sequential)] internal struct RectF { public float left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Quad { public float x1,y1,x2,y2,x3,y3,x4,y4; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PauseCallback(IntPtr pause);
    [StructLayout(LayoutKind.Sequential)] internal struct Pause { public int version; public PauseCallback NeedToPauseNow; public IntPtr user; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteCallback(IntPtr writer, IntPtr data, uint size);
    [StructLayout(LayoutKind.Sequential)] internal struct Writer { public int version; public WriteCallback WriteBlock; }
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path,[MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr bytes,nuint size,[MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDF_GetLastError();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_GetPageSizeByIndex(IntPtr doc,int index,out double width,out double height);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDF_LoadPage(IntPtr doc,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_Create(int width,int height,int alpha);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFBitmap_FillRect(IntPtr bitmap,int left,int top,int width,int height,uint color);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_RenderPageBitmap_Start(IntPtr bitmap,IntPtr page,int x,int y,int width,int height,int rotate,int flags,ref Pause pause);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_RenderPage_Continue(IntPtr page,ref Pause pause);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_RenderPage_Close(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_DeviceToPage(IntPtr page,int startX,int startY,int sizeX,int sizeY,int rotate,int deviceX,int deviceY,out double pageX,out double pageY);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_PageToDevice(IntPtr page,int startX,int startY,int sizeX,int sizeY,int rotate,double pageX,double pageY,out int deviceX,out int deviceY);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc,IntPtr bookmark);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc,IntPtr bookmark);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark,IntPtr buffer,uint bytes);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBookmark_GetDest(IntPtr doc,IntPtr bookmark);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDFAction_GetType(IntPtr action);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFAction_GetDest(IntPtr doc,IntPtr action);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFDest_GetDestPageIndex(IntPtr doc,IntPtr dest);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDFDest_GetView(IntPtr dest,out uint count,[Out] float[] parameters);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFDest_GetLocationInPage(IntPtr dest,out int hasX,out int hasY,out int hasZoom,out float x,out float y,out float zoom);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFPage_GetAnnot(IntPtr page,int index);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern uint FPDFAnnot_GetStringValue(IntPtr annot,[MarshalAs(UnmanagedType.LPUTF8Str)] string key,IntPtr buffer,uint bytes);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_SetStringValue(IntPtr annot,[MarshalAs(UnmanagedType.LPUTF8Str)] string key,[MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_GetRect(IntPtr annot,out RectF rect);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_SetRect(IntPtr annot,ref RectF rect);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_SetColor(IntPtr annot,int colorType,uint r,uint g,uint b,uint a);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFPage_CreateAnnot(IntPtr page,int subtype);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot,ref Quad quad);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_SaveAsCopy(IntPtr doc,ref Writer writer,uint flags);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFText_ClosePage(IntPtr text);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_CountChars(IntPtr text);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_GetCharBox(IntPtr text,int index,out double left,out double right,out double bottom,out double top);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr FPDFText_FindStart(IntPtr text,[MarshalAs(UnmanagedType.LPWStr)] string query,uint flags,int start);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_FindNext(IntPtr search);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFText_FindClose(IntPtr search);
}
