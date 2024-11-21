using ConsumidorRoboPhub;

Console.WriteLine("Processo de leitura iniciado");

var consumer = new RabbitMqConsumer();
consumer.IniciarConsumo();

Console.WriteLine(" [*] Aguardando mensagens. Pressione [enter] para sair.");
Console.ReadLine(); // Mantém o console aberto para ouvir novas mensagens