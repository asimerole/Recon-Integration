namespace Recon.Core.Domain.Models;

public class ReconFile : BaseFile // DAILY, RPUSK, RNET, DIAGN
{
    protected override Task ProcessContentSpecificAsync()
    {
        // Для одиночних файлів унікальної логіки немає. 
        // Вся робота зроблена в BaseFile (читання BinaryData).
        return Task.CompletedTask; 
    }
}