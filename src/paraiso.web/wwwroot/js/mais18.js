document.addEventListener('DOMContentLoaded', function() {
    const modal = document.getElementById('ageVerificationModal');
    const enterButton = document.getElementById('enterSite');

    // Verificar se o cookie existe
    if (!document.cookie.includes('AgeVerified=true')) {
        modal.style.display = 'block';
        document.body.style.overflow = 'hidden';
    }

    enterButton.addEventListener('click', function() {
        // Criar cookie com validade de 30 dias
        const date = new Date();
        date.setTime(date.getTime() + (30 * 24 * 60 * 60 * 1000));
        document.cookie = "AgeVerified=true; expires=" + date.toUTCString() + "; path=/";

        modal.style.display = 'none';
        document.body.style.overflow = 'auto';
    });
});